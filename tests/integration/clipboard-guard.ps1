# The clipboard guard: a test that must write the user's clipboard takes it whole first, writes only
# while it still holds what was taken, and puts it back proven — or says, in one word, what state it
# left the clipboard in. Dot-sourced by win32-control.ps1; exercised against a fake by
# clipboard-guard.tests.ps1 (no clipboard access there).
#
# The states, by CONDITION, once (every caller quotes these words, none invents its own):
#   Take()                      the snapshot: every format's bytes and the sequence number they were read
#                               under; or Unsupported (a format that is not global memory), nothing written;
#                               throws when the clipboard cannot be opened.
#   WriteSentinel(text, snap)   `unopened`  — nothing touched.
#                               `failed`    — EmptyClipboard refused under the open: nothing touched.
#                               `changed`   — the sequence moved since the snapshot: nothing touched. Or a
#                                             copy replaced the sentinel before the second open below
#                                             (theirs is kept; the snapshot was already replaced by it,
#                                             as any copy would have).
#                               `written`   — the sentinel is in; Sequence is ITS generation: the number
#                                             the system reports AFTER the close. (CloseClipboard adds the
#                                             formats it synthesizes — CF_TEXT, CF_OEMTEXT, CF_LOCALE beside
#                                             a CF_UNICODETEXT — and moves the sequence once per format, so
#                                             the number read under the writer's open is NOT final: live it
#                                             is three behind.) The generation is read under a SECOND open
#                                             that finds the clipboard holding exactly the sentinel; when
#                                             that open is refused, Sequence is the number read under the
#                                             writer's open and Detail says `generation unverified` — the
#                                             restore then proves ownership by content.
#                               `put back`  — the sentinel could not be set after the clipboard was
#                                             emptied; the snapshot was put back and PROVEN under the
#                                             same open. Nothing lost; the case cannot run.
#                               `mutated`   — emptied, and neither the sentinel nor the snapshot could be
#                                             set and proven: the clipboard holds neither reliably.
#   Restore(snap, write)        `unopened`  — nothing touched (retry).
#                               `changed`   — the clipboard is not ours: the sequence is not the write's
#                                             generation AND the clipboard does not hold exactly the
#                                             sentinel (CF_UNICODETEXT = its text; every other format one
#                                             the system synthesizes from it). Someone wrote since the
#                                             sentinel; theirs is kept, nothing touched.
#                               `restored`  — the snapshot is back, PROVEN by a read-back under the same
#                                             open.
#                               `mutated`   — the snapshot is not back: the clipboard was emptied and
#                                             the snapshot could not be set or proven, or it could not
#                                             be emptied and still holds the sentinel. NEVER retried with
#                                             the old write (the sequence moved because WE emptied it and
#                                             the sentinel is gone; a retry would read that as `changed`
#                                             and call a wrecked clipboard "kept").
# A `mutated` anywhere is CLIPBOARD NOT RESTORED for the run: the case FAILs regardless of -Strict, the
# snapshot file is kept, and the run's teardown is not proven (no `--cleanup-confirmed` release).
# The snapshot file is DPAPI-protected for the current user (CryptProtectData): a plain temp file would
# be a second copy of whatever the user last copied. Recovery: win32-control.ps1 -RestoreClipboard <file>.

# Add-Type cannot replace a loaded type: a ClipboardGuard from an older run of this file in the same
# shell would run its old C# under the new source with no warning. Revision below is bumped with every
# change to the C#, and a loaded type that does not carry it stops the run.
$clipboardGuardRevision = 2
if (('Agwinterm.Win32ControlTest.ClipboardGuard' -as [type]) -and
    ('Agwinterm.Win32ControlTest.ClipboardGuard' -as [type])::Revision -ne $clipboardGuardRevision) {
    throw "a ClipboardGuard type from an older run of this file is loaded in this shell (revision $(('Agwinterm.Win32ControlTest.ClipboardGuard' -as [type])::Revision), source $clipboardGuardRevision); run it from a fresh pwsh"
}
if (-not ('Agwinterm.Win32ControlTest.ClipboardGuard' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Agwinterm.Win32ControlTest
{
    /// <summary>The five calls the guard makes on a clipboard. Native below; a fake in the tests.
    /// Data flows as byte arrays: the api owns the handles.</summary>
    public interface IClipboardApi
    {
        bool Open();
        void Close();
        bool Empty();
        uint[] Formats();
        /// <summary>The bytes of a format, or null when the data cannot be read, sized, or locked
        /// (a non-HGLOBAL, a delayed render whose owner is gone).</summary>
        byte[] Get(uint format);
        bool Set(uint format, byte[] bytes);
        uint Sequence();
    }

    public sealed class NativeClipboardApi : IClipboardApi
    {
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern uint EnumClipboardFormats(uint format);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalFree(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalUnlock(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr GlobalSize(IntPtr h);

        const uint GMEM_MOVEABLE = 0x0002;

        public bool Open() { return OpenClipboard(IntPtr.Zero); }
        public void Close() { CloseClipboard(); }
        public bool Empty() { return EmptyClipboard(); }
        public uint Sequence() { return GetClipboardSequenceNumber(); }

        public uint[] Formats()
        {
            var formats = new List<uint>();
            for (uint f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f)) formats.Add(f);
            return formats.ToArray();
        }

        public byte[] Get(uint format)
        {
            IntPtr h = GetClipboardData(format);
            if (h == IntPtr.Zero) return null;
            ulong size = (ulong)GlobalSize(h);
            if (size == 0 || size > int.MaxValue) return null;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(p, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { GlobalUnlock(h); }
        }

        public bool Set(uint format, byte[] bytes)
        {
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(ulong)bytes.Length);
            if (h == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return false; }
            try { Marshal.Copy(bytes, 0, p, bytes.Length); } finally { GlobalUnlock(h); }
            if (SetClipboardData(format, h) == IntPtr.Zero) { GlobalFree(h); return false; }
            return true;   // the system owns the handle now
        }
    }

    /// <summary>Every format on the clipboard with its bytes, taken under one OpenClipboard, and the
    /// sequence number they were read under. <c>Unsupported</c> names a format whose data is not a
    /// global memory block (a GDI bitmap or metafile, a palette, owner-display, the private range),
    /// whose handle the system would not own (the GDI-object range), or that could not be read: such
    /// a clipboard cannot be put back faithfully, so the case that would replace it must not run.
    /// Every image clipboard is one (ClipboardGuard.IsUnsupported).</summary>
    public sealed class ClipboardSnapshot
    {
        public uint[] Formats = new uint[0];
        public byte[][] Data = new byte[0][];
        public uint Sequence;
        public string Unsupported;

        /// <summary>Format ids and names, never contents: for a Check detail.</summary>
        public string Names
        {
            get
            {
                if (Formats.Length == 0) return "(empty)";
                var sb = new StringBuilder();
                for (int i = 0; i < Formats.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(Formats[i]).Append('=').Append(ClipboardGuard.FormatName(Formats[i])).Append('[').Append(Data[i].Length).Append(']');
                }
                return sb.ToString();
            }
        }

        /// <summary>Same formats (any order) with the same bytes each.</summary>
        public bool SameAs(ClipboardSnapshot other)
        {
            if (other == null || other.Unsupported != null || other.Formats.Length != Formats.Length) return false;
            for (int i = 0; i < Formats.Length; i++)
            {
                int j = Array.IndexOf(other.Formats, Formats[i]);
                if (j < 0 || other.Data[j].Length != Data[i].Length) return false;
                for (int k = 0; k < Data[i].Length; k++) if (other.Data[j][k] != Data[i][k]) return false;
            }
            return true;
        }

        [StructLayout(LayoutKind.Sequential)] struct DATA_BLOB { public int cbData; public IntPtr pbData; }
        [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CryptProtectData(ref DATA_BLOB dataIn, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DATA_BLOB dataOut);
        [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DATA_BLOB dataOut);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
        const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        static byte[] Dpapi(byte[] input, bool protect)
        {
            var blobIn = new DATA_BLOB { cbData = input.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, input.Length)) };
            try
            {
                Marshal.Copy(input, 0, blobIn.pbData, input.Length);
                DATA_BLOB blobOut;
                bool ok = protect
                    ? CryptProtectData(ref blobIn, "agwinterm clipboard snapshot", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out blobOut)
                    : CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out blobOut);
                if (!ok) throw new InvalidOperationException((protect ? "CryptProtectData" : "CryptUnprotectData") + " failed: " + Marshal.GetLastWin32Error());
                try
                {
                    var output = new byte[blobOut.cbData];
                    Marshal.Copy(blobOut.pbData, output, 0, output.Length);
                    return output;
                }
                finally { LocalFree(blobOut.pbData); }
            }
            finally { Marshal.FreeHGlobal(blobIn.pbData); }
        }

        /// <summary>Recovery copy on disk, DPAPI-protected for the current user: per format, an id
        /// and a length (little-endian uint32) then the bytes. Kept whenever the restore was not
        /// proven, deleted only after it was.</summary>
        public void Save(string path)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                for (int i = 0; i < Formats.Length; i++)
                {
                    w.Write(Formats[i]);
                    w.Write((uint)Data[i].Length);
                    w.Write(Data[i]);
                }
            }
            byte[] sealed_ = Dpapi(ms.ToArray(), true);
            using (var f = new FileStream(path, FileMode.CreateNew)) f.Write(sealed_, 0, sealed_.Length);
        }

        public static ClipboardSnapshot Load(string path)
        {
            byte[] plain = Dpapi(File.ReadAllBytes(path), false);
            var formats = new List<uint>();
            var data = new List<byte[]>();
            using (var r = new BinaryReader(new MemoryStream(plain)))
            {
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    formats.Add(r.ReadUInt32());
                    int n = (int)r.ReadUInt32();
                    data.Add(r.ReadBytes(n));
                }
            }
            return new ClipboardSnapshot { Formats = formats.ToArray(), Data = data.ToArray() };
        }
    }

    /// <summary>What a write left behind: one of the states in the file header, the generation the
    /// clipboard holds after it (meaningful for `written` and `put back`), and the failure text.</summary>
    public sealed class ClipboardWrite
    {
        public string State;
        public uint Sequence;
        public string Detail;
        /// <summary>The sentinel text a WriteSentinel put in: the restore's proof of ownership when
        /// the sequence number moved without a user's write.</summary>
        public string Sentinel;
        public override string ToString() { return Detail == null ? State : State + ": " + Detail; }
    }

    public static class ClipboardGuard
    {
        /// <summary>Bumped with every change to this C#; the .ps1 refuses a loaded type without it.</summary>
        public const int Revision = 2;
        public const uint CF_UNICODETEXT = 13;

        /// <summary>The clipboard the guard talks to: the native one unless a test swaps in a fake.</summary>
        public static IClipboardApi Api = new NativeClipboardApi();

        public static string FormatName(uint f)
        {
            switch (f)
            {
                case 1: return "CF_TEXT"; case 2: return "CF_BITMAP"; case 3: return "CF_METAFILEPICT"; case 4: return "CF_SYLK";
                case 5: return "CF_DIF"; case 6: return "CF_TIFF"; case 7: return "CF_OEMTEXT"; case 8: return "CF_DIB";
                case 9: return "CF_PALETTE"; case 10: return "CF_PENDATA"; case 11: return "CF_RIFF"; case 12: return "CF_WAVE";
                case 13: return "CF_UNICODETEXT"; case 14: return "CF_ENHMETAFILE"; case 15: return "CF_HDROP"; case 16: return "CF_LOCALE";
                case 17: return "CF_DIBV5"; case 0x80: return "CF_OWNERDISPLAY"; case 0x81: return "CF_DSPTEXT";
                case 0x82: return "CF_DSPBITMAP"; case 0x83: return "CF_DSPMETAFILEPICT"; case 0x8E: return "CF_DSPENHMETAFILE";
            }
            if (f >= 0x200 && f <= 0x2FF) return "CF_PRIVATE";
            if (f >= 0x300 && f <= 0x3FF) return "CF_GDIOBJ";
            return RegisteredName(f);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern int GetClipboardFormatNameW(uint format, StringBuilder name, int max);
        static string RegisteredName(uint f)
        {
            try
            {
                var sb = new StringBuilder(256);
                return GetClipboardFormatNameW(f, sb, sb.Capacity) > 0 ? sb.ToString() : "?";
            }
            catch (Exception) { return "?"; }
        }

        // What cannot be taken as bytes and put back. A GDI handle (CF_BITMAP, the metafiles), a
        // palette, owner-display and the private range (0x200-0x2FF) are not global memory. The GDI-
        // object range (0x300-0x3FF) IS global memory, but the system does not free those handles
        // when the clipboard is emptied, so putting them back would take on an ownership the
        // original owner had: declined. An IMAGE clipboard never runs the case: Windows synthesizes
        // CF_PALETTE (9) beside any CF_DIB/CF_DIBV5 and CF_BITMAP beside any DIB (EnumClipboardFormats
        // lists synthesized formats), so a DIB always brings a format from this list with it — the
        // snapshot names the first one met, and nothing is written.
        static bool IsUnsupported(uint f)
        {
            return f == 2 || f == 3 || f == 9 || f == 14 || f == 0x80 || f == 0x82 || f == 0x83 || f == 0x8E || (f >= 0x200 && f <= 0x3FF);
        }

        static bool TryOpen()
        {
            for (int i = 0; i < 40; i++)
            {
                if (Api.Open()) return true;
                Thread.Sleep(25);
            }
            return false;
        }

        // Under an already open clipboard.
        static ClipboardSnapshot TakeOpen()
        {
            uint[] all = Api.Formats();
            var keep = new List<uint>();
            var data = new List<byte[]>();
            foreach (uint f in all)
            {
                if (IsUnsupported(f)) return new ClipboardSnapshot { Unsupported = "format " + f + " (" + FormatName(f) + ")" };
                byte[] bytes = Api.Get(f);
                if (bytes == null) return new ClipboardSnapshot { Unsupported = "format " + f + " (" + FormatName(f) + "), whose data could not be read" };
                keep.Add(f);
                data.Add(bytes);
            }
            return new ClipboardSnapshot { Formats = keep.ToArray(), Data = data.ToArray(), Sequence = Api.Sequence() };
        }

        /// <summary>Every format's bytes and the sequence number they were read under, or a snapshot
        /// whose Unsupported names why it cannot be taken whole. Throws when the clipboard cannot be
        /// opened; nothing is written either way.</summary>
        public static ClipboardSnapshot Take()
        {
            if (!TryOpen()) throw new InvalidOperationException("the clipboard could not be opened (another window holds it)");
            try { return TakeOpen(); }
            finally { Api.Close(); }
        }

        // Under an already open clipboard: empty it and set every format of the snapshot. Null on
        // success, else what failed. After a false Empty() nothing was touched; after a failed Set
        // the clipboard IS emptied (partially filled) — the caller decides what that means.
        static string Put(ClipboardSnapshot snap, out bool emptied)
        {
            emptied = false;
            if (!Api.Empty()) return "EmptyClipboard failed";
            emptied = true;
            for (int i = 0; i < snap.Formats.Length; i++)
            {
                if (!Api.Set(snap.Formats[i], snap.Data[i]))
                    return "SetClipboardData failed for format " + snap.Formats[i] + " (" + FormatName(snap.Formats[i]) + ")";
            }
            return null;
        }

        // Put, once more on a failure, then proven by a read-back under the same open. Null when the
        // clipboard holds exactly the snapshot, else why not (and the clipboard is in an unknown state).
        static string PutProven(ClipboardSnapshot snap)
        {
            bool emptied;
            string why = Put(snap, out emptied);
            if (why != null) why = Put(snap, out emptied);
            if (why != null) return why;
            ClipboardSnapshot back = TakeOpen();
            if (back.Unsupported != null) return "the read-back holds " + back.Unsupported;
            if (!snap.SameAs(back)) return "the read-back differs from the snapshot (before=" + snap.Names + " after=" + back.Names + ")";
            return null;
        }

        /// <summary>Replaces the clipboard with <paramref name="text"/> alone, only while it still
        /// holds what <paramref name="snap"/> was taken from. The states are in the file header.</summary>
        public static ClipboardWrite WriteSentinel(string text, ClipboardSnapshot snap)
        {
            if (!TryOpen()) return new ClipboardWrite { State = "unopened", Detail = "the clipboard could not be opened (another window holds it)" };
            uint inside;
            try
            {
                uint now = Api.Sequence();
                if (now != snap.Sequence) return new ClipboardWrite { State = "changed", Sequence = now, Detail = "the clipboard was written to after the snapshot (sequence " + snap.Sequence + " -> " + now + ")" };
                var sentinel = new ClipboardSnapshot { Formats = new[] { CF_UNICODETEXT }, Data = new[] { Encoding.Unicode.GetBytes(text + "\0") } };
                bool emptied;
                string why = Put(sentinel, out emptied);
                if (why == null) inside = Api.Sequence();
                else
                {
                    if (!emptied) return new ClipboardWrite { State = "failed", Sequence = now, Detail = why + "; nothing was touched" };
                    string back = PutProven(snap);
                    if (back == null) return new ClipboardWrite { State = "put back", Sequence = Api.Sequence(), Detail = why + "; the snapshot was put back and proven" };
                    return new ClipboardWrite { State = "mutated", Sequence = Api.Sequence(), Detail = why + "; putting the snapshot back failed too: " + back };
                }
            }
            finally { Api.Close(); }
            return Generation(text, inside);
        }

        // After the close. The sequence read under the writer's open is not the sentinel's generation:
        // CloseClipboard adds the formats the system synthesizes from CF_UNICODETEXT (CF_TEXT,
        // CF_OEMTEXT, CF_LOCALE) and moves the sequence once per format — measured live: inside 3920,
        // after the close 3923, and stable from then on (reading the synthesized formats does not move
        // it; putting all four explicitly, as a restore does, adds nothing). The generation is therefore
        // read under a second open that finds the clipboard holding exactly the sentinel. A copy made
        // between the close and that open shows as other content: `changed`, theirs kept.
        static ClipboardWrite Generation(string text, uint inside)
        {
            uint after = Api.Sequence();
            if (after == inside) return new ClipboardWrite { State = "written", Sequence = inside, Sentinel = text };
            if (!TryOpen()) return new ClipboardWrite { State = "written", Sequence = inside, Sentinel = text, Detail = "generation unverified: the close moved the sequence " + inside + " -> " + after + " and the clipboard could not be reopened; the restore proves ownership by content" };
            try
            {
                ClipboardSnapshot back = TakeOpen();
                uint now = Api.Sequence();
                if (IsSentinel(back, text)) return new ClipboardWrite { State = "written", Sequence = now, Sentinel = text, Detail = "generation " + now + " (the close moved the sequence " + inside + " -> " + now + "; the sentinel is in)" };
                return new ClipboardWrite { State = "changed", Sequence = now, Sentinel = text, Detail = "a copy replaced the sentinel before its generation could be read (sequence " + inside + " -> " + now + "; the clipboard holds " + (back.Unsupported ?? back.Names) + "); theirs is kept" };
            }
            finally { Api.Close(); }
        }

        // Exactly our sentinel: CF_UNICODETEXT is its text and every other format is one the system
        // synthesizes from it. Any other format, or other text, is someone's copy.
        static bool IsSentinel(ClipboardSnapshot back, string text)
        {
            if (text == null || back.Unsupported != null) return false;
            bool textSeen = false;
            for (int i = 0; i < back.Formats.Length; i++)
            {
                uint f = back.Formats[i];
                if (f == CF_UNICODETEXT)
                {
                    byte[] want = Encoding.Unicode.GetBytes(text + "\0");
                    if (back.Data[i].Length != want.Length) return false;
                    for (int j = 0; j < want.Length; j++) if (back.Data[i][j] != want[j]) return false;
                    textSeen = true;
                }
                else if (f != 1 && f != 7 && f != 16) return false;
            }
            return textSeen;
        }

        /// <summary>Puts the snapshot back, under one OpenClipboard, only while the clipboard still
        /// holds what <paramref name="write"/> put in — its generation, or exactly its sentinel when
        /// the number moved (a generation the write could not verify) — and proves it by a read-back
        /// under the same open. The states are in the file header.</summary>
        public static ClipboardWrite Restore(ClipboardSnapshot snap, ClipboardWrite write)
        {
            if (!TryOpen()) return new ClipboardWrite { State = "unopened", Detail = "the clipboard could not be opened (another window holds it)" };
            try
            {
                uint now = Api.Sequence();
                if (now != write.Sequence)
                {
                    ClipboardSnapshot held = TakeOpen();
                    if (!IsSentinel(held, write.Sentinel)) return new ClipboardWrite { State = "changed", Sequence = now, Detail = "someone wrote to the clipboard during the case (sequence " + write.Sequence + " -> " + now + "; it holds " + (held.Unsupported ?? held.Names) + "); theirs is kept" };
                }
                string why = PutProven(snap);
                if (why == null) return new ClipboardWrite { State = "restored", Sequence = Api.Sequence() };
                return new ClipboardWrite { State = "mutated", Sequence = Api.Sequence(), Detail = why };
            }
            finally { Api.Close(); }
        }

        /// <summary>Recovery: the snapshot goes back whatever the clipboard holds now (the user asked
        /// for it), proven by a read-back. Returns `restored`, `unopened` or `mutated`.</summary>
        public static ClipboardWrite Force(ClipboardSnapshot snap)
        {
            if (!TryOpen()) return new ClipboardWrite { State = "unopened", Detail = "the clipboard could not be opened (another window holds it)" };
            try
            {
                string why = PutProven(snap);
                if (why == null) return new ClipboardWrite { State = "restored", Sequence = Api.Sequence() };
                return new ClipboardWrite { State = "mutated", Sequence = Api.Sequence(), Detail = why };
            }
            finally { Api.Close(); }
        }
    }

    /// <summary>An in-memory clipboard for the guard's tests: the store, a sequence number that moves
    /// on every write, the close-time synthesis the real one does (a CF_UNICODETEXT set under an open
    /// gains CF_TEXT, CF_OEMTEXT and CF_LOCALE at the close, one sequence step each — the live
    /// measurement behind `written`'s generation), and faults a test can arm — a number of Set calls to
    /// fail after the next Empty (a partial put), Empty itself failing, and opens refused from the
    /// Nth on.</summary>
    public sealed class FakeClipboardApi : IClipboardApi
    {
        public readonly Dictionary<uint, byte[]> Store = new Dictionary<uint, byte[]>();
        public uint Seq = 1000;
        public bool IsOpen;
        public bool OpenFails;
        /// <summary>When above zero, every Open() after this many successful ones is refused.</summary>
        public int FailOpensAfter;
        public bool EmptyFails;
        /// <summary>How many Set calls fail (consecutively) from now; each failure counts down.</summary>
        public int SetFailures;
        public int Opens, Empties, Sets;
        bool unicodeSetUnderThisOpen;

        public bool Open() { if (OpenFails || (FailOpensAfter > 0 && Opens >= FailOpensAfter)) return false; if (IsOpen) throw new InvalidOperationException("opened twice"); IsOpen = true; Opens++; unicodeSetUnderThisOpen = false; return true; }
        public void Close()
        {
            if (!IsOpen) throw new InvalidOperationException("closed while not open");
            IsOpen = false;
            if (!unicodeSetUnderThisOpen) return;
            string text = Encoding.Unicode.GetString(Store[13]).TrimEnd('\0');
            if (!Store.ContainsKey(1)) { Store[1] = Encoding.ASCII.GetBytes(text + "\0"); Seq++; }
            if (!Store.ContainsKey(7)) { Store[7] = Encoding.ASCII.GetBytes(text + "\0"); Seq++; }
            if (!Store.ContainsKey(16)) { Store[16] = new byte[] { 9, 4, 0, 0 }; Seq++; }
        }
        void Held() { if (!IsOpen) throw new InvalidOperationException("clipboard call while not open"); }
        public bool Empty() { Held(); Empties++; if (EmptyFails) return false; Store.Clear(); Seq++; unicodeSetUnderThisOpen = false; return true; }
        public uint[] Formats() { Held(); var keys = new List<uint>(Store.Keys); return keys.ToArray(); }
        public byte[] Get(uint format) { Held(); byte[] b; return Store.TryGetValue(format, out b) ? (byte[])b.Clone() : null; }
        public bool Set(uint format, byte[] bytes)
        {
            Held(); Sets++;
            if (SetFailures > 0) { SetFailures--; return false; }
            Store[format] = (byte[])bytes.Clone(); Seq++;
            if (format == 13) unicodeSetUnderThisOpen = true;
            return true;
        }
        public uint Sequence() { return Seq; }

        /// <summary>A user's copy: from OUTSIDE the guard's transactions (between two of them).</summary>
        public void UserWrites(uint format, byte[] bytes) { if (IsOpen) throw new InvalidOperationException("a user cannot write while the guard holds the clipboard"); Store.Clear(); Store[format] = bytes; Seq++; }
        public string Names { get { var s = new ClipboardSnapshot(); var f = new List<uint>(Store.Keys); var d = new List<byte[]>(); foreach (var k in f) d.Add(Store[k]); s.Formats = f.ToArray(); s.Data = d.ToArray(); return s.Names; } }
    }
}
'@
}

# The restore, as the case runs it: retried only while nothing was touched (`unopened`), never after a
# `mutated` (the sequence moved because WE emptied it: a retry with the old number would say
# `changed` and call a wrecked clipboard "kept"). The recovery file is KEPT only on `mutated` (the
# one state where it is the only copy) and deleted otherwise: after `restored` the clipboard holds it,
# after `changed` the user's newer copy replaced it as any copy would have, and a run that could not
# open the clipboard five times over wrote nothing.
function Invoke-ClipboardRestore($snap, $write, [string]$file) {
    $result = $null
    for ($i = 0; $i -lt 5; $i++) {
        $result = [Agwinterm.Win32ControlTest.ClipboardGuard]::Restore($snap, $write)
        if ($result.State -ne 'unopened') { break }
        Start-Sleep -Milliseconds 200
    }
    if ($result.State -ne 'mutated' -and $file) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
    $result
}

# Recovery for a run that printed CLIPBOARD NOT RESTORED: the file's snapshot goes back whatever the
# clipboard holds now, proven by a read-back; the file is deleted only then.
function Restore-ClipboardFile([string]$file) {
    $snap = [Agwinterm.Win32ControlTest.ClipboardSnapshot]::Load($file)
    "restoring $($snap.Names) from $file"
    $result = [Agwinterm.Win32ControlTest.ClipboardGuard]::Force($snap)
    if ($result.State -eq 'restored') { Remove-Item -LiteralPath $file -Force; "restored; $file deleted" }
    else { "NOT restored ($result); $file kept" }
    $result
}
