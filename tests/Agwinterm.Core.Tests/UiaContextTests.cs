using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Agwinterm.Core.Tests;

/// <summary>Actual built providers, without HWNDs, a UI process, or a screen-reader connection.
/// Tests context/range ownership and callback retirement, not Narrator's presentation.</summary>
public class UiaContextTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly Ui = LoadUi();
    private static Assembly LoadUi()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Agwinterm.Win32"))) root = Path.GetDirectoryName(root);
        return Assembly.LoadFrom(Directory.GetFiles(Path.Combine(root!, "src", "Agwinterm.Win32", "bin"), "Agwinterm.Win32.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First());
    }
    private static Type Type(string name) => Ui.GetType("Agwinterm.Win32." + name, true)!;
    private static object New(string name, params object[] args) => Activator.CreateInstance(Type(name), Flags, null, args, null)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static void Callback(object owner, string fieldName, Func<object?> fn)
    {
        var field = owner.GetType().GetField(fieldName, Flags)!;
        var invoke = field.FieldType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        Expression body = Expression.Invoke(Expression.Constant(fn));
        body = invoke.ReturnType == typeof(void) ? Expression.Block(body, Expression.Empty()) : Expression.Convert(body, invoke.ReturnType);
        field.SetValue(owner, Expression.Lambda(field.FieldType, body, parameters).Compile());
    }
    private static object Snapshot(string text)
    {
        var s = New("Uia+TextSnapshot");
        Set(s, "Lines", new[] { text }); Set(s, "LineStart", new[] { 0, text.Length + 1 });
        Set(s, "TotalLength", text.Length); Set(s, "VisibleRows", 1);
        return s;
    }
    private static void Closed(Action action)
    {
        var ex = Assert.Throws<TargetInvocationException>(action);
        Assert.Equal(unchecked((int)0x80040201), Assert.IsType<COMException>(ex.InnerException).HResult);
    }

    [Fact]
    public void LibraryAndQuickContextsKeepTheirOwnTextAndRetainedRanges()
    {
        var a = New("Uia"); var b = New("Uia"); var quick = New("Uia");
        string aText = "library-A";
        Callback(a, "GetTextSnapshot", () => Snapshot(aText));
        Callback(b, "GetTextSnapshot", () => Snapshot("library-B"));
        Callback(quick, "GetTextSnapshot", () => Snapshot("quick-pane"));
        var ra = New("UiaTextRange", a, 0, 100); var rb = New("UiaTextRange", b, 0, 100); var rq = New("UiaTextRange", quick, 0, 100);
        try
        {
            Assert.Equal("library-A", Call(ra, "GetText", -1)); Assert.Equal("library-B", Call(rb, "GetText", -1)); Assert.Equal("quick-pane", Call(rq, "GetText", -1));
            Assert.Equal(0, Call(ra, "Compare", rb));
            var start = Enum.ToObject(Type("TextPatternRangeEndpoint"), 0);
            Assert.IsType<ArgumentException>(Assert.Throws<TargetInvocationException>(() => Call(ra, "CompareEndpoints", start, rb, start)).InnerException);
            aText = "A-updated"; Assert.Equal(aText, Call(ra, "GetText", -1));
            ((IDisposable)b).Dispose(); Closed(() => Call(rb, "GetText", -1));
            Closed(() => Call(rb, "Clone")); Closed(() => Call(rb, "Compare", rb));
            Assert.Equal(aText, Call(ra, "GetText", -1)); Assert.Equal("quick-pane", Call(rq, "GetText", -1));
            ((IDisposable)a).Dispose(); Closed(() => Call(ra, "GetText", -1));
            Assert.Equal("quick-pane", Call(rq, "GetText", -1));
        }
        finally { ((IDisposable)a).Dispose(); ((IDisposable)b).Dispose(); ((IDisposable)quick).Dispose(); }
    }

    [Fact]
    public void SessionFragmentsKeepTokensAcrossReorderingAndRetireOnRemoval()
    {
        var owner = New("Uia"); var kind = Enum.ToObject(Type("Uia+NodeKind"), 3);
        object Node(int token, double x) { var n = New("Uia+Node"); Set(n, "Kind", kind); Set(n, "Index", token); var r = New("UiaRect"); Set(r, "Left", x); Set(n, "Rect", r); return n; }
        var first = Node(11, 10); var retained = Node(22, 20);
        object[] current = [first, retained];
        Callback(owner, "GetTree", () => { var t = New("Uia+TreeSnapshot"); var nodes = Array.CreateInstance(Type("Uia+Node"), current.Length); for (int i = 0; i < current.Length; i++) nodes.SetValue(current[i], i); Set(t, "Nodes", nodes); return t; });
        var fragment = New("UiaFragment", owner, kind, 22);
        try
        {
            current = [retained, first];
            var rect = Call(fragment, "GetBoundingRectangle")!;
            Assert.Equal(20d, rect.GetType().GetField("Left")!.GetValue(rect));
            current = [first];
            Closed(() => Call(fragment, "GetBoundingRectangle")); Closed(() => Call(fragment, "SetFocus"));
        }
        finally { ((IDisposable)owner).Dispose(); }
    }

    [Fact]
    public void RetainedButtonsCannotInvokeAnotherWindowOrKeepClosedCallbacks()
    {
        var a = New("Uia"); var b = New("Uia"); int callsA = 0, callsB = 0;
        Callback(a, "OnInvoke", () => { callsA++; return null; }); Callback(b, "OnInvoke", () => { callsB++; return null; });
        var kind = Enum.ToObject(Type("Uia+NodeKind"), 4);
        var buttonA = New("UiaButton", a, kind, 0); var buttonB = New("UiaButton", b, kind, 0);
        try
        {
            Assert.NotEqual(a.GetType().GetProperty("Identity", Flags)!.GetValue(a), b.GetType().GetProperty("Identity", Flags)!.GetValue(b));
            Call(buttonA, "Invoke"); Assert.Equal(1, callsA); Assert.Equal(0, callsB);
            ((IDisposable)a).Dispose(); Closed(() => Call(buttonA, "Invoke"));
            foreach (string field in new[] { "GetTree", "GetVisibleText", "GetTextSnapshot", "OnInvoke", "OnSetFocus" }) Assert.Null(a.GetType().GetField(field, Flags)!.GetValue(a));
            Call(buttonB, "Invoke"); Assert.Equal(1, callsB);
        }
        finally { ((IDisposable)a).Dispose(); ((IDisposable)b).Dispose(); }
    }
}
