using System.Reflection;
using System.Runtime.CompilerServices;

namespace Agwinterm.Core.Tests;

public class BuiltinBoxGlyphTests
{
    [Fact]
    public void GateMatchesTheActualRendererSwitch()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Agwinterm.Win32")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var assemblyPath = Directory.GetFiles(Path.Combine(root!, "src", "Agwinterm.Win32", "bin"),
            "Agwinterm.Win32.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        var ui = Assembly.LoadFrom(assemblyPath);
        var program = ui.GetType("Agwinterm.Win32.Program", true)!;
        var draw = program.GetMethod("DrawBoxGlyph", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var instance = RuntimeHelpers.GetUninitializedObject(program);
        var color = Activator.CreateInstance(draw.GetParameters()[7].ParameterType);
        for (int cp = 0x24FF; cp <= 0x25A0; cp++)
        {
            // A supported arm immediately uses the brush; an unsupported arm must return false
            // without touching graphics. Null graphics keep this a headless switch-coverage test.
            object?[] args = { null, null, cp, 20f, 30f, 8f, 16f, color };
            if (BuiltinBoxGlyphs.Supports(cp))
                Assert.IsType<NullReferenceException>(Assert.Throws<TargetInvocationException>(() => draw.Invoke(instance, args)).InnerException);
            else Assert.Equal(false, draw.Invoke(instance, args));
        }
    }
}
