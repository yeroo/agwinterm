namespace Agwinterm.Core;

public interface IParserPerformer
{
    void Print(char ch);
    void Execute(byte control);
    void CsiDispatch(char final, IReadOnlyList<int> parameters, char prefix);
    void EscDispatch(char final);
    /// <summary>ESC with an intermediate byte (0x20-0x2F) before the final: <c>ESC ( 0</c> designates the
    /// DEC line-drawing set into G0, <c>ESC ( B</c> puts ASCII back.</summary>
    void EscDispatch(char intermediate, char final);
    void OscDispatch(int command, string text);
    void ApcDispatch(string data);
    /// <summary>A completed DCS string (ESC P … ST), raw bytes without the introducer/terminator.</summary>
    void DcsDispatch(byte[] data);
}
