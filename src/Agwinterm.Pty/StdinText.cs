using System.Text;

namespace Agwinterm.Pty;

/// <summary>
/// `agwintermctl session type --stdin` — the text to type is standard input, taken as bytes.
///
/// The argv path cannot carry everything: the CLI joins positionals with a single space, so a run
/// of spaces collapses; the option splitter eats a positional that starts with `--`; and a quote or
/// a newline has to survive two shells' quoting rules before it reaches us. Every one of those
/// losses is silent — the call answers ok and the shell receives something else. Reading stdin as
/// raw bytes sidesteps all of it.
///
/// Invalid UTF-8 is REFUSED, not replaced. The server reads its pipe through a StreamReader with the
/// default replacement fallback, so by the time bad bytes reach it they are already U+FFFD and it
/// answers ok — a replacement character arriving in a shell is exactly the "succeeded, wrong
/// content" failure this exists to prevent. The refusal names the byte offset of the first bad
/// sequence so the caller can find it. Nothing is sent on a refusal.
///
/// This lives in Agwinterm.Pty rather than the CLI's Main so it is unit-testable without a pipe
/// (VersionReport is the precedent).
/// </summary>
public static class StdinText
{
    /// <summary>What the read produced. <see cref="Text"/> is set exactly when <see cref="Error"/>
    /// is null; an empty stream is an empty <see cref="Text"/>, not an error.</summary>
    public sealed record Outcome(string? Text, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>Read <paramref name="stdin"/> to the end and decode it (see <see cref="Decode"/>).</summary>
    public static Outcome Read(Stream stdin)
    {
        using var ms = new MemoryStream();
        stdin.CopyTo(ms);
        return Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    /// <summary>
    /// Decode <paramref name="bytes"/> as strict UTF-8.
    ///
    /// Two deliberate edits to the bytes, and only these two:
    ///
    /// A leading UTF-8 BOM (EF BB BF) is dropped. It is an encoding signature, not text — a file
    /// saved by Notepad or PowerShell 5's redirection carries one — and typed into a shell it is an
    /// invisible zero-width character sitting in front of the command.
    ///
    /// Exactly ONE trailing newline (LF or CR LF) is stripped. A here-string, `echo`, and PowerShell's
    /// pipe to a native process each add exactly one, and the caller almost never means it: with it
    /// kept, every `--stdin` call would also press Enter. Only one is stripped because a caller who
    /// means two must be able to send two — a command plus its Enter is "text\n\n" on the wire.
    /// Someone will read this as a bug one day; it is the trade that makes the common case right.
    /// </summary>
    public static Outcome Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];

        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            int offset = FirstInvalidOffset(bytes);
            return new Outcome(null,
                $"invalid UTF-8 at byte offset {offset} (0x{bytes[offset]:X2}); stdin must be UTF-8 text");
        }

        if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
        else if (text.EndsWith('\n')) text = text[..^1];
        return new Outcome(text, null);
    }

    /// <summary>Byte offset of the first invalid sequence. Computed with the span decoder rather than
    /// read off DecoderFallbackException.Index, whose meaning is not pinned down by its docs — and
    /// the offset is the one thing the refusal must get right. A truncated multi-byte sequence at
    /// the very end counts as invalid (final block), and its offset is where the sequence starts.</summary>
    private static int FirstInvalidOffset(ReadOnlySpan<byte> bytes)
    {
        var dest = new char[bytes.Length + 1];
        var status = System.Text.Unicode.Utf8.ToUtf16(bytes, dest, out int consumed, out _,
            replaceInvalidSequences: false, isFinalBlock: true);
        return status == System.Buffers.OperationStatus.InvalidData ? consumed : bytes.Length - 1;
    }
}
