using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Agwinterm.Win32;

// UIA fragment tree (T2-14 Stage 3): exposes agwinterm's controls as a navigable, focusable element
// tree so Narrator/NVDA can scan/Tab through them as first-class elements —
//   Root (window) ─┬─ Terminal (Document + Text pattern)
//                  └─ Sidebar (List) ── Session (ListItem) × N
// Fragments are lightweight: each holds its (kind,index) identity and re-reads a fresh snapshot
// (Uia.Tree(), built by the app on the UIA thread) to answer navigation/properties/bounds. Stable
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
    internal enum NodeKind { Root, Terminal, Sidebar, Session }

    /// <summary>One node in the accessibility tree snapshot (built by Program.BuildUiaTree).</summary>
    internal sealed class Node
    {
        public NodeKind Kind;
        public int Index;                 // session index (Session nodes)
        public string Name = "";
        public bool Focused, Selected;
        public UiaRect Rect;              // screen px (all zero → fall back to the host/window rect)
        public int Parent = -1;          // index into TreeSnapshot.Nodes
        public int[] Children = Array.Empty<int>();
    }

    internal sealed class TreeSnapshot { public required Node[] Nodes; }   // Nodes[0] is the root

    internal static Func<TreeSnapshot>? GetTree;
    internal static Action<NodeKind, int>? OnSetFocus;   // app moves internal focus when UIA calls SetFocus
    private static readonly TreeSnapshot EmptyTree = new()
    { Nodes = new[] { new Node { Kind = NodeKind.Root, Name = "agwinterm" } } };
    internal static TreeSnapshot Tree() { try { return GetTree?.Invoke() ?? EmptyTree; } catch { return EmptyTree; } }

    /// <summary>Find a node by identity (kind + index) in a snapshot; null if it's gone (e.g. closed session).</summary>
    internal static Node? Find(TreeSnapshot t, NodeKind kind, int index)
    {
        foreach (var n in t.Nodes) if (n.Kind == kind && n.Index == index) return n;
        return null;
    }
    internal static int IndexOf(TreeSnapshot t, Node n) => Array.IndexOf(t.Nodes, n);

    /// <summary>A COM fragment pointer (IRawElementProviderFragment*, AddRef'd) for a node, or 0.</summary>
    internal static nint FragmentPtr(NodeKind kind, int index)
    {
        object obj = kind switch
        {
            NodeKind.Root => new UiaRoot(_treeHwnd),
            NodeKind.Terminal => new UiaTerminal(_treeHwnd),
            _ => new UiaFragment(kind, index),
        };
        return AsInterface(obj, IID_IRawElementProviderFragment);
    }

    internal static readonly Guid IID_IRawElementProviderFragment = new("f7063da8-8359-439c-9297-bbc5299a7d87");
    internal static readonly Guid IID_IRawElementProviderFragmentRoot = new("620ce2a5-ab8f-40a9-86cb-de3c75599b58");
    internal static nint _treeHwnd;   // set in OnGetObject

    /// <summary>Raise a UIA focus-changed event on a tree node (so the reader announces it) — used when
    /// F6 / arrow keys move the internal focus.</summary>
    internal static void RaiseFocus(NodeKind kind, int index)
    {
        if (_providerSimple == 0 || !ClientsListening) return;
        nint frag = 0;
        try { frag = FragmentPtr(kind, index); if (frag != 0) UiaRaiseAutomationEvent(frag, UIA_AutomationFocusChangedEventId); }
        catch { }
        finally { if (frag != 0) Marshal.Release(frag); }
    }

    private const int UIA_AutomationFocusChangedEventId = 20005;

    // UIA control-type ids + common property ids (shared by all fragments).
    internal const int CT_Pane = 50033, CT_Document = 50030, CT_List = 50008, CT_ListItem = 50007;
    internal const int P_ControlType = 30003, P_Name = 30005, P_LocalizedControlType = 30004,
        P_IsControlElement = 30016, P_IsContentElement = 30017, P_IsKeyboardFocusable = 30009,
        P_HasKeyboardFocus = 30008;
}

// ---- Fragment implementations ----

/// <summary>Shared property/navigation logic over a tree node identified by (kind,index).</summary>
internal abstract class UiaNodeBase
{
    protected readonly Uia.NodeKind Kind;
    protected readonly int Index;
    protected UiaNodeBase(Uia.NodeKind kind, int index) { Kind = kind; Index = index; }

    protected Uia.Node? Self(Uia.TreeSnapshot t) => Uia.Find(t, Kind, Index);

    public nint Navigate(NavigateDirection direction)
    {
        var t = Uia.Tree();
        var me = Self(t);
        if (me is null) return 0;
        int myIdx = Uia.IndexOf(t, me);
        switch (direction)
        {
            case NavigateDirection.Parent:
                return me.Parent >= 0 ? Uia.FragmentPtr(t.Nodes[me.Parent].Kind, t.Nodes[me.Parent].Index) : 0;
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

    private static nint NodePtr(Uia.TreeSnapshot t, int nodeIdx) => Uia.FragmentPtr(t.Nodes[nodeIdx].Kind, t.Nodes[nodeIdx].Index);

    public nint GetRuntimeId() => UiaArrays.IntArray(new[] { 42, (int)Kind, Index });   // stable identity

    public UiaRect GetBoundingRectangle() { var n = Self(Uia.Tree()); return n?.Rect ?? default; }

    public nint GetEmbeddedFragmentRoots() => 0;

    public void SetFocus() { try { Uia.OnSetFocus?.Invoke(Kind, Index); } catch { } }

    public nint GetFragmentRoot() => Uia.FragmentRootPtr();

    protected void FillProperty(int propertyId, nint pRetVal, int controlType, string localized, bool content)
    {
        var n = Self(Uia.Tree());
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
    public UiaRoot(nint hwnd) : base(Uia.NodeKind.Root, 0) => _hwnd = hwnd;

    public ProviderOptions GetProviderOptions() => ProviderOptions.ServerSideProvider;
    public nint GetPatternProvider(int patternId) => 0;
    public nint GetHostRawElementProvider() => UiaHostProviderFromHwnd(_hwnd, out nint host) >= 0 ? host : 0;
    public void GetPropertyValue(int propertyId, nint pRetVal) { VariantInit(pRetVal); FillProperty(propertyId, pRetVal, Uia.CT_Pane, "terminal window", true); }

    // FragmentRoot
    public nint ElementProviderFromPoint(double x, double y)
    {
        var t = Uia.Tree();
        // deepest node whose rect contains the point (sessions/terminal before the containers)
        for (int i = t.Nodes.Length - 1; i >= 1; i--)
        {
            var r = t.Nodes[i].Rect;
            if (r.Width > 0 && x >= r.Left && x < r.Left + r.Width && y >= r.Top && y < r.Top + r.Height)
                return Uia.FragmentPtr(t.Nodes[i].Kind, t.Nodes[i].Index);
        }
        return 0;
    }
    public nint GetFocus()
    {
        var t = Uia.Tree();
        foreach (var n in t.Nodes) if (n.Focused) return Uia.FragmentPtr(n.Kind, n.Index);
        return 0;
    }

    [LibraryImport("uiautomationcore.dll")] private static partial int UiaHostProviderFromHwnd(nint hwnd, out nint provider);
    [LibraryImport("oleaut32.dll")] private static partial void VariantInit(nint pvarg);
}

[GeneratedComClass]
internal partial class UiaTerminal : UiaNodeBase, IRawElementProviderSimple, IRawElementProviderFragment
{
    private readonly nint _hwnd;
    public UiaTerminal(nint hwnd) : base(Uia.NodeKind.Terminal, 0) => _hwnd = hwnd;

    public ProviderOptions GetProviderOptions() => ProviderOptions.ServerSideProvider;
    public nint GetPatternProvider(int patternId) => patternId == 10014 /* Text */ ? Uia.AsInterface(this, Uia.IID_ITextProvider) : 0;
    public nint GetHostRawElementProvider() => 0;   // hosted via the root
    public void GetPropertyValue(int propertyId, nint pRetVal)
    {
        VariantInit(pRetVal);
        // Name = live screen text (so a plain read still speaks the terminal); rest via FillProperty.
        if (propertyId == Uia.P_Name)
        {
            string t = Uia.GetVisibleText?.Invoke() is { Length: > 0 } v ? v : "terminal";
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
    public UiaFragment(Uia.NodeKind kind, int index) : base(kind, index) { }

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
            _ => (Uia.CT_Pane, "group", false),
        };
        FillProperty(propertyId, pRetVal, ct, lct, content);
    }
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
