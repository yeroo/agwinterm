namespace Agwinterm.Core;

public interface IParserPerformer
{
    void Print(char ch);
    void Execute(byte control);
    void CsiDispatch(char final, IReadOnlyList<int> parameters, char prefix);
    void EscDispatch(char final);
    void OscDispatch(int command, string text);
    void ApcDispatch(string data);
}
