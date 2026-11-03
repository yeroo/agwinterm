using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

/// <summary>The menu bar's data (MenuModel) is agterm's menus; these pin the list so a drift from
/// agterm's order or wording is a failing test, not a diff nobody reads.</summary>
public class MenuModelTests
{
    [Fact] public void FourMenusInAgtermsOrderWithUniqueMnemonics()
    {
        Assert.Equal(new[] { "File", "View", "Navigate", "Help" }, MenuModel.Menus.Select(m => m.Title));
        Assert.Equal(new[] { 'F', 'V', 'N', 'H' }, MenuModel.Menus.Select(m => m.Mnemonic));
        Assert.Equal(4, MenuModel.Menus.Select(m => m.Mnemonic).Distinct().Count());
    }

    [Theory]
    [InlineData('F', 0)][InlineData('f', 0)][InlineData('V', 1)][InlineData('n', 2)][InlineData('H', 3)]
    [InlineData('X', -1)][InlineData(' ', -1)]
    public void MnemonicOpensItsMenu(char letter, int menu) => Assert.Equal(menu, MenuModel.MenuForMnemonic(letter));

    [Fact] public void FileMenuIsAgterms()
    {
        Assert.Equal(new[]
        {
            "New Window", "Open Window", "Rename Window…", "Delete Window", "-",
            "New Workspace", "Rename Workspace", "Delete Workspace", "-",
            "New Session", "Open Directory…", "Open Recent", "Reopen Last Closed Item", "Rename Session",
            "Duplicate Session", "Session Link", "Reveal in Explorer", "Close Session", "Reopen Closed Item", "Clear Status", "-",
            "Edit Keymap…", "Reload Keymap", "Edit agwinterm.conf…", "Reload Config",
        }, MenuModel.Menus[0].Items.Select(i => i.Label));
    }

    [Fact] public void ViewMenuIsAgtermsLessTheItemsAgwintermHasNoCounterpartFor()
    {
        Assert.Equal(new[]
        {
            "Increase Font Size", "Decrease Font Size", "Actual Size", "Select Theme…", "-",
            "Hide Sidebar", "Expand Workspaces", "Collapse Workspaces", "Collapse Workspace",
            "Show Flagged Sessions", "Flag Session", "Clear Flagged", "Focus Workspace",
            "Toggle Vertical Split", "Toggle Horizontal Split", "Swap Panes", "Show Scratch", "Find…", "Quick Terminal", "-",
            "Toggle Fullscreen",
        }, MenuModel.Menus[1].Items.Select(i => i.Label));
    }

    [Fact] public void NavigateMenuIsAgterms()
    {
        Assert.Equal(new[]
        {
            "Go to Session", "Command Palette", "Custom Commands", "Go to Attention…", "Dashboard", "-",
            "Previous Session", "Next Session", "Previous Attention Session", "Next Attention Session",
            "First Session", "Last Session", "Previous Workspace", "Next Workspace", "Previous Window", "Next Window", "-",
            "Focus Left Pane", "Focus Right Pane",
        }, MenuModel.Menus[2].Items.Select(i => i.Label));
    }

    [Fact] public void HelpMenuIsAgtermsPlusShellIntegrationUpdatesAndAbout()
    {
        Assert.Equal(new[]
        {
            "Developer Documentation…", "-",
            "Install Command Line Tool…", "Install Agent Status Hooks…", "Install Agent Skill…", "Install Shell Integration…", "-",
            "Check for Updates…", "About agwinterm",
        }, MenuModel.Menus[3].Items.Select(i => i.Label));
    }

    [Fact] public void EveryShortcutRowNamesABuiltinKeymapActionOrAHardwiredOne()
    {
        var hardwired = new[] { "increase_font_size", "decrease_font_size", "reset_font_size", "dashboard" };
        foreach (var item in MenuModel.Menus.SelectMany(m => m.Items).Where(i => i.Action is not null))
            Assert.True(Keymap.IsBuiltinAction(item.Action!) || hardwired.Contains(item.Action), $"{item.Label}: {item.Action}");
    }

    [Fact] public void RowIdsAreUniqueAndStateRowsHaveBothLabels()
    {
        var ids = MenuModel.Menus.SelectMany(m => m.Items).Where(i => !i.IsSeparator).Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var item in MenuModel.Menus.SelectMany(m => m.Items).Where(i => i.AltLabel is not null))
            Assert.NotEqual(item.Label, item.AltLabel);
        Assert.Equal(MenuFlyout.OpenWindow, MenuModel.Menus[0].Items.Single(i => i.Id == "open_window").Flyout);
        Assert.Equal(MenuFlyout.OpenRecent, MenuModel.Menus[0].Items.Single(i => i.Id == "open_recent").Flyout);
        Assert.Equal(MenuFlyout.SessionLink, MenuModel.Menus[0].Items.Single(i => i.Id == "session_link").Flyout);
    }

    [Theory]
    [InlineData("ctrl+shift+t", "Ctrl+Shift+T")]
    [InlineData("ctrl+backtick", "Ctrl+`")]
    [InlineData("f11", "F11")]
    [InlineData("ctrl+alt+left", "Ctrl+Alt+Left")]
    [InlineData("shift+comma", "Shift+,")]
    [InlineData("ctrl+equals", "Ctrl+=")]
    [InlineData("escape", "Esc")]
    public void ChordsDisplayAsPeopleWriteThem(string chord, string shown) => Assert.Equal(shown, Keymap.DisplayChord(chord));
}
