namespace Agwinterm.Core.Tests;

public class TerminalFontFamilyTests
{
    [Fact]
    public void InstalledConfiguredFamilyWins()
        => Assert.Equal("User Mono", TerminalFontFamily.Resolve("User Mono", _ => true));

    [Theory]
    [InlineData("Cascadia Mono")]
    [InlineData("Consolas")]
    [InlineData("Lucida Console")]
    [InlineData("Courier New")]
    public void MissingFamilyUsesAnActuallyInstalledMonospaceFace(string installed)
        => Assert.Equal(installed, TerminalFontFamily.Resolve("Missing", candidate => candidate == installed));

    [Fact]
    public void FallbackPriorityIsDeterministic()
        => Assert.Equal("Cascadia Mono", TerminalFontFamily.Resolve("Missing", candidate => candidate != "Missing"));

    [Fact]
    public void MissingFallbackFailsExplicitlyInsteadOfChoosingAnUnknownFace()
        => Assert.Throws<InvalidOperationException>(() => TerminalFontFamily.Resolve("Missing", _ => false));

    [Theory]
    [InlineData(0x10)] [InlineData(0x11)] [InlineData(0x12)]
    [InlineData(0x5B)] [InlineData(0x5C)]
    [InlineData(0xA0)] [InlineData(0xA1)] [InlineData(0xA2)]
    [InlineData(0xA3)] [InlineData(0xA4)] [InlineData(0xA5)]
    public void ModifierEventsDoNotRepresentEditing(int vk) => Assert.True(Keymap.IsModifierKey(vk));

    [Theory]
    [InlineData(0x43)] [InlineData(0x0D)] [InlineData(0x70)]
    public void ActualKeysRemainEditingEvents(int vk) => Assert.False(Keymap.IsModifierKey(vk));
}
