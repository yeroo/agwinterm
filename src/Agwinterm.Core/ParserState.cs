namespace Agwinterm.Core;

internal enum ParserState
{
    Ground,
    Escape,
    CsiEntry,
    CsiParam,
    CsiIntermediate,
    CsiIgnore,
    OscString,
    OscEsc,
    ApcString,
    ApcEsc,
    DcsString,
    DcsEsc,
}
