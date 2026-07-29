using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Agwinterm.Core.Tests;

/// <summary>
/// Guards the whole P/Invoke text-marshalling bug class across the UI assembly.
///
/// A [DllImport] with no CharSet defaults to Ansi. For a text OUT parameter that means the runtime
/// hands the OS a buffer of one BYTE per element while the callee is told it has that many WCHARs —
/// so a W entry point writes UTF-16 straight off the end of the block. That is exactly how
/// ToUnicodeEx (win32-input-mode, DECSET ?9001) smashed the heap on ordinary typing: the app died
/// with STATUS_HEAP_CORRUPTION (0xC0000374) inside ntdll a few hundred keystrokes in, and the
/// translated character came back as 0 the whole time, so the feature was silently dead too.
///
/// Every P/Invoke taking string / char[] / StringBuilder must therefore declare CharSet.Unicode.
/// </summary>
public class PInvokeCharSetTests
{
    /// <summary>Locate the built Agwinterm.Win32 assembly (CI builds the solution before testing).</summary>
    private static string FindUiAssembly()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "Agwinterm.Win32")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var bin = Path.Combine(dir!, "src", "Agwinterm.Win32", "bin");
        Assert.True(Directory.Exists(bin), $"build Agwinterm.Win32 first (no {bin})");
        var hits = Directory.GetFiles(bin, "Agwinterm.Win32.dll", SearchOption.AllDirectories)
                            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        Assert.True(hits.Length > 0, $"build Agwinterm.Win32 first (no dll under {bin})");
        return hits[0];
    }

    private static bool IsText(Type t) => t == typeof(string) || t == typeof(char[]) || t == typeof(StringBuilder)
                                       || t == typeof(string[]) || t == typeof(char).MakeByRefType();

    [Fact]
    public void EveryPInvokeWithTextParameters_DeclaresUnicodeCharSet()
    {
        var asm = Assembly.LoadFrom(FindUiAssembly());
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                               | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        int checkedCount = 0;

        foreach (var t in types)
        foreach (var m in t.GetMethods(All))
        {
            if (!m.Attributes.HasFlag(MethodAttributes.PinvokeImpl)) continue;
            var di = m.GetCustomAttribute<DllImportAttribute>();
            if (di is null) continue;   // metadata-only import; nothing to assert
            checkedCount++;

            var textParams = m.GetParameters().Where(p => IsText(p.ParameterType)).ToArray();
            if (textParams.Length > 0 && di.CharSet != CharSet.Unicode)
                offenders.Add($"{t.Name}.{m.Name} (CharSet={di.CharSet}, "
                            + $"text params: {string.Join(", ", textParams.Select(p => p.Name))})");
        }

        Assert.True(checkedCount > 50, $"expected the UI's P/Invoke surface, found only {checkedCount}");
        Assert.True(offenders.Count == 0,
            "P/Invokes with text parameters must declare CharSet.Unicode — Ansi marshalling of a W "
            + "entry point's OUT buffer overruns the heap:\n  " + string.Join("\n  ", offenders));
    }
}
