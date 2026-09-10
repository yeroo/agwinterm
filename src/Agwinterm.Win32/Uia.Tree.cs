using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Agwinterm.Win32;

// UIA fragment tree (T2-14 Stage 3): exposes agwinterm's controls as a navigable, focusable element
// tree so Narrator/NVDA can scan/Tab through them as first-class elements —
//   Root (window) ─┬─ Terminal (Document + Text pattern)
//                  └─ Sidebar (List) ── Session (ListItem) × N
// Fragments are lightweight: each holds its (kind,index) identity and re-reads a fresh snapshot
// (Owner.Tree(), marshaled to the owning UI thread) to answer navigation/properties/bounds. Stable
// RuntimeIds let UIA correlate the freshly-built fragments across calls.

internal enum NavigateDirection { Parent = 0, NextSibling = 1, PreviousSibling = 2, FirstChild = 3, LastChild = 4 }

[StructLayout(LayoutKind.Sequential)]
internal struct UiaRect { public double Left, Top, Width, Height; }

[GeneratedComInterface]
[Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface IRawElementProviderFragment
{
    nint Navigate(NavigateDirection direction);   // IRawElementProviderFragment*
    nint GetRuntimeId();                            // SAFEARRAY(int)
    UiaRect GetBoundingRectangle();
    nint GetEmbeddedFragmentRoots();                // SAFEARRAY
    void SetFocus();
    nint GetFragmentRoot();                          // IRawElementProviderFragmentRoot*
}

[GeneratedComInterface]
[Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IRawElementProviderFragmentRoot
{
    nint ElementProviderFromPoint(double x, double y);   // IRawElementProviderFragment*
    nint GetFocus();                                       // IRawElementProviderFragment*
}

partial class Uia
{
    internal enum NodeKind { Root, Terminal, Sidebar, Session, ChromeButton, SettingsGroup, SettingsControl, SettingsTab, HelpDoc }

    /// <summary>One node in the accessibility tree snapshot (built by Program.BuildUiaTree).</summary>
    internal sealed class Node
    {
        public NodeKind Kind;
        public int Index;                 // session lifetime token; per-kind ordinal for fixed controls
        public string Name = "";
        public bool Focused, Selected;
        public UiaRect Rect;              // screen px (all zero → fall back to the host/window rect)
        public int Parent = -1;          // index into TreeSnapshot.Nodes
        public int[] Children = Array.Empty<int>();
    }

    private static bool IsButton(NodeKind k) => k is NodeKind.ChromeButton or NodeKind.SettingsControl or NodeKind.SettingsTab;

    internal sealed class TreeSnapshot { public required Node[] Nodes; }   // Nodes[0] is the root

    internal Func<TreeSnapshot>? GetTree;
    internal Action<NodeKind, int>? OnSetFocus;   // app moves internal focus when UIA calls SetFocus
    private static readonly TreeSnapshot EmptyTree = new()
    { Nodes = new[] { new Node { Kind = NodeKind.Root, Name = "agwinterm" } } };
    internal TreeSnapshot Tree() { EnsureAlive(); var tree = GetTree?.Invoke() ?? EmptyTree; EnsureAlive(); return tree; }

    /// <summary>Find a node by identity (kind + index) in a snapshot; null if it's gone (e.g. closed session).</summary>
    internal static Node? Find(TreeSnapshot t, NodeKind kind, int index)
    {
        foreach (var n in t.Nodes) if (n.Kind == kind && n.Index == index) return n;
        return null;
    }
    internal static int IndexOf(TreeSnapshot t, Node n) => Array.IndexOf(t.Nodes, n);

    /// <summary>A COM fragment pointer (IRawElementProviderFragment*, AddRef'd) for a node, or 0.</summary>
    internal nint FragmentPtr(NodeKind kind, int index)
    {
        EnsureAlive();
        object obj = kind switch
        {
            NodeKind.Root => new UiaRoot(this, _treeHwnd),
            NodeKind.Terminal => new UiaTerminal(this),
            _ when IsButton(kind) => new UiaButton(this, kind, index),
            _ => new UiaFragment(this, kind, index),
        };
        return AsInterface(obj, IID_IRawElementProviderFragment);
    }

    internal Action<NodeKind, int>? OnInvoke;   // app activates a button/control when UIA invokes it
    internal static readonly Guid IID_IInvokeProvider = new("54fcb24b-e18e-47a2-b4d3-eccbe77599a2");

    internal static readonly Guid IID_IRawElementProviderFragment = new("f7063da8-8359-439c-9297-bbc5299a7d87");
    internal static readonly Guid IID_IRawElementProviderFragmentRoot = new("620ce2a5-ab8f-40a9-86cb-de3c75599b58");
    internal nint _treeHwnd;   // set in OnGetObject

    /// <summary>Raise a UIA focus-changed event on a tree node (so the reader announces it) — used when
    /// F6 / arrow keys move the internal focus.</summary>
    internal void RaiseFocus(NodeKind kind, int index)
    {
        if (_closed || _providerSimple == 0 || !ClientsListening) return;
        nint frag = 0;
        try { frag = FragmentPtr(kind, index); if (frag != 0) UiaRaiseAutomationEvent(frag, UIA_AutomationFocusChangedEventId); }
        catch { }
        finally { if (frag != 0) Marshal.Release(frag); }
    }

    private const int UIA_AutomationFocusChangedEventId = 20005;

    // UIA control-type ids + common property ids (shared by all fragments).
    internal const int CT_Pane = 50033, CT_Document = 50030, CT_List = 50008, CT_ListItem = 50007,
        CT_Button = 50000, CT_Group = 50026, CT_TabItem = 50019;
    internal const int P_ControlType = 30003, P_Name = 30005, P_LocalizedControlType = 30004,
        P_IsControlElement = 30016, P_IsContentElement = 30017, P_IsKeyboardFocusable = 30009,
        P_HasKeyboardFocus = 30008;
}

// ---- Fragment implementations ----

/// <summary>Shared property/navigation logic over a tree node identified by (kind,index).</summary>
internal abstract class UiaNodeBase
{
    protected readonly Uia Owner;
    protected readonly Uia.NodeKind Kind;
    protected readonly int Index;
    protected UiaNodeBase(Uia owner, Uia.NodeKind kind, int index) { Owner = owner; Kind = kind; Index = index; }

    protected Uia.Node? Self(Uia.TreeSnapshot t)
    {
        var node = Uia.Find(t, Kind, Index);
        if (node is null && Kind == Uia.NodeKind.Session)
            throw new COMException("The accessibility session is closed.", unchecked((int)0x80040201));
        return node;
    }

    public nint Navigate(NavigateDirection direction)
    {
        var t = Owner.Tree();
        var me = Self(t);
        if (me is null) return 0;
        int myIdx = Uia.IndexOf(t, me);
        switch (direction)
        {
            case NavigateDirection.Parent:
                return me.Parent >= 0 ? Owner.FragmentPtr(t.Nodes[me.Parent].Kind, t.Nodes[me.Parent].Index) : 0;
            case NavigateDirection.FirstChild:
                return me.Children.Length > 0 ? NodePtr(t, me.Children[0]) : 0;
            case NavigateDirection.LastChild:
                return me.Children.Length > 0 ? NodePtr(t, me.Children[^1]) : 0;
            case NavigateDirection.NextSibling:
            case NavigateDirection.PreviousSibling:
                {
                    if (me.Parent < 0) return 0;
                    var sib = t.Nodes[me.Parent].Children;
                    int at = Array.IndexOf(sib, myIdx);
                    int to = direction == NavigateDirection.NextSibling ? at + 1 : at - 1;
                    return at >= 0 && to >= 0 && to < sib.Length ? NodePtr(t, sib[to]) : 0;
                }
        }
        return 0;
    }

    private nint NodePtr(Uia.TreeSnapshot t, int nodeIdx) => Owner.FragmentPtr(t.Nodes[nodeIdx].Kind, t.Nodes[nodeIdx].Index);

    public nint GetRuntimeId()
    {
        Owner.EnsureAlive();
        // HWND roots use the host identity; children append to it (UiaAppendRuntimeId = 3).
        return Kind == Uia.NodeKind.Root ? 0 : UiaArrays.IntArray(new[] { 3, Owner.Identity, (int)Kind, Index });
    }

    public UiaRect GetBoundingRectangle() { var n = Self(Owner.Tree()); return n?.Rect ?? default; }

    public nint GetEmbeddedFragmentRoots() => 0;

    public void SetFocus() { Owner.EnsureAlive(); if (Kind == Uia.NodeKind.Session) Self(Owner.Tree()); Owner.OnSetFocus?.Invoke(Kind, Index); }

    public nint GetFragmentRoot() => Owner.FragmentRootPtr();

    protected void FillProperty(int propertyId, nint pRetVal, int controlType, string localized, bool content)
    {
        var n = Self(Owner.Tree());
        object? val = propertyId switch
        {
            Uia.P_ControlType => controlType,
            Uia.P_Name => n?.Name is { Length: > 0 } nm ? nm : localized,
            Uia.P_LocalizedControlType => localized,
            Uia.P_IsControlElement or Uia.P_IsKeyboardFocusable => true,
            Uia.P_IsContentElement => content,
            Uia.P_HasKeyboardFocus => n?.Focused == true,
            _ => null,
        };
        if (val is not null) Marshal.GetNativeVariantForObject(val, pRetVal);
    }
}

[GeneratedComClass]
internal partial class UiaRoot : UiaNodeBase, IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot
{
    private readonly nint _hwnd;
    public UiaRoot(Uia owner, nint hwnd) : base(owner, Uia.NodeKind.Root, 0) => _hwnd = hwnd;

    public ProviderOptions GetProviderOptions() => ProviderOptions.ServerSideProvider;
    public nint GetPatternProvider(int patternId) => 0;
    public nint GetHostRawElementProvider() { Owner.EnsureAlive(); return UiaHostProviderFromHwnd(_hwnd, out nint host) >= 0 ? host : 0; }
    public void GetPropertyValue(int propertyId, nint pRetVal) { VariantInit(pRetVal); FillProperty(propertyId, pRetVal, Uia.CT_Pane, "terminal window", true); }

    // FragmentRoot
    public nint ElementProviderFromPoint(double x, double y)
    {
        var t = Owner.Tree();
        // deepest node whose rect contains the point (sessions/terminal before the containers)
        for (int i = t.Nodes.Length - 1; i >= 1; i--)
        {
            var r = t.Nodes[i].Rect;
            if (r.Width > 0 && x >= r.Left && x < r.Left + r.Width && y >= r.Top && y < r.Top + r.Height)
                return Owner.FragmentPtr(t.Nodes[i].Kind, t.Nodes[i].Index);
        }
        return 0;
    }
    public nint GetFocus()
    {
        var t = Owner.Tree();
        foreach (var n in t.Nodes) if (n.Focused) return Owner.FragmentPtr(n.Kind, n.Index);
        return 0;
    }

    [LibraryImport("uiautomationcore.dll")] private static partial int UiaHostProviderFromHwnd(nint hwnd, out nint provider);
    [LibraryImport("oleaut32.dll")] private static partial void VariantInit(nint pvarg);
}

[GeneratedComClass]
internal partial class UiaTerminal : UiaNodeBase, IRawElementProviderSimple, IRawElementProviderFragment
{
    public UiaTerminal(Uia owner) : base(owner, Uia.NodeKind.Terminal, 0) { }

    public ProviderOptions GetProviderOptions() => ProviderOptions.ServerSideProvider;
    public nint GetPatternProvider(int patternId) => patternId == 10014 /* Text */ ? Uia.AsInterface(this, Uia.IID_ITextProvider) : 0;
    public nint GetHostRawElementProvider() => 0;   // hosted via the root
    public void GetPropertyValue(int propertyId, nint pRetVal)
    {
        VariantInit(pRetVal);
        // Name = live screen text (so a plain read still speaks the terminal); rest via FillProperty.
        if (propertyId == Uia.P_Name)
        {
            string t = Owner.VisibleText() is { Length: > 0 } v ? v : "terminal";
            Marshal.GetNativeVariantForObject(t, pRetVal);
            return;
        }
        FillProperty(propertyId, pRetVal, Uia.CT_Document, "terminal", true);
    }
    [LibraryImport("oleaut32.dll")] private static partial void VariantInit(nint pvarg);
}

[GeneratedComClass]
internal partial class UiaFragment : UiaNodeBase, IRawElementProviderSimple, IRawElementProviderFragment
{
    public UiaFragment(Uia owner, Uia.NodeKind kind, int index) : base(owner, kind, index) { }

    public ProviderOptions GetProviderOptions() => ProviderOptions.ServerSideProvider;
    public nint GetPatternProvider(int patternId) => 0;
    public nint GetHostRawElementProvider() => 0;
    public void GetPropertyValue(int propertyId, nint pRetVal)
    {
        VariantInit(pRetVal);
        var (ct, lct, content) = Kind switch
        {
            Uia.NodeKind.Sidebar => (Uia.CT_List, "session list", false),
            Uia.NodeKind.Session => (Uia.CT_ListItem, "session", true),
            Uia.NodeKind.SettingsGroup => (Uia.CT_Group, "settings", false),
            Uia.NodeKind.HelpDoc => (Uia.CT_Document, "help", true),
            _ => (Uia.CT_Pane, "group", false),
        };
        FillProperty(propertyId, pRetVal, ct, lct, content);
    }
    [LibraryImport("oleaut32.dll")] private static partial void VariantInit(nint pvarg);
}

/// <summary>UIA Invoke pattern — lets a screen reader activate a button/control.</summary>
[GeneratedComInterface]
[Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2")]
internal partial interface IInvokeProvider { void Invoke(); }

/// <summary>A focusable, invokable Button element — chrome buttons (Quick/Scratch terminal, Split,
/// Settings, …) and the settings-dialog controls. Invoke runs the same action a click would.</summary>
[GeneratedComClass]
internal partial class UiaButton : UiaNodeBase, IRawElementProviderSimple, IRawElementProviderFragment, IInvokeProvider
{
    public UiaButton(Uia owner, Uia.NodeKind kind, int index) : base(owner, kind, index) { }

    public ProviderOptions GetProviderOptions() => ProviderOptions.ServerSideProvider;
    public nint GetPatternProvider(int patternId) => patternId == 10000 /* Invoke */ ? Uia.AsInterface(this, Uia.IID_IInvokeProvider) : 0;
    public nint GetHostRawElementProvider() => 0;
    public void GetPropertyValue(int propertyId, nint pRetVal)
    {
        VariantInit(pRetVal);
        var (ct, lct) = Kind == Uia.NodeKind.SettingsTab ? (Uia.CT_TabItem, "tab") : (Uia.CT_Button, "button");
        FillProperty(propertyId, pRetVal, ct, lct, true);
    }
    public void Invoke() { Owner.EnsureAlive(); Owner.OnInvoke?.Invoke(Kind, Index); }
    [LibraryImport("oleaut32.dll")] private static partial void VariantInit(nint pvarg);
}

internal static partial class UiaArrays
{
    private const ushort VT_I4 = 3;
    public static nint IntArray(int[] vals)
    {
        nint sa = SafeArrayCreateVector(VT_I4, 0, (uint)vals.Length);
        if (sa != 0 && vals.Length > 0 && SafeArrayAccessData(sa, out nint data) >= 0)
        {
            Marshal.Copy(vals, 0, data, vals.Length);
            SafeArrayUnaccessData(sa);
        }
        return sa;
    }
}
