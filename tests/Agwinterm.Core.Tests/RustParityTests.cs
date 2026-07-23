using System.Runtime.InteropServices;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>
/// Differential oracle for the Rust port of the emulator core (native/agwinterm-core): the C#
/// implementation is the reference; the Rust crate must agree EXACTLY, stage by stage, before it
/// takes over. Tests here run only when the crate has been built (cargo build --release) — absent
/// dll = skipped (the Rust port is opt-in until CI builds it).
/// </summary>
public class RustParityTests
{
    private static readonly nint Lib = TryLoad();

    private static nint TryLoad()
    {
        // Walk up from the test bin dir to the repo root (the dir holding Agwinterm.slnx).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agwinterm.slnx"))) dir = dir.Parent;
        if (dir is null) return 0;
        string dll = Path.Combine(dir.FullName, "native", "agwinterm-core", "target", "release", "agwinterm_core.dll");
        return File.Exists(dll) && NativeLibrary.TryLoad(dll, out nint h) ? h : 0;
    }

    private static T Fn<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Lib, name));

    private delegate uint AbiVersion();
    private delegate byte WcwidthOf(uint cp);

    [Fact]
    public void AbiVersion_Matches()
    {
        if (Lib == 0) return;   // crate not built — differential run is opt-in
        Assert.Equal(1u, Fn<AbiVersion>("agwcore_abi_version")());
    }

    [Fact]
    public void Wcwidth_AgreesForEveryCodepoint()
    {
        if (Lib == 0) return;   // crate not built — differential run is opt-in
        var native = Fn<WcwidthOf>("agwcore_wcwidth");
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            int expected = Wcwidth.Of(cp);
            int actual = native((uint)cp);
            if (expected != actual)   // assert only on mismatch: 1.1M Assert.Equal calls are slow
                Assert.Fail($"wcwidth mismatch at U+{cp:X4}: C#={expected} rust={actual}");
        }
    }
}
