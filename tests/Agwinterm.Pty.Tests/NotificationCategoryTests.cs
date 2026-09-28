using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class NotificationCategoryTests
{
    [Theory]
    [InlineData("ok", NotificationCategory.Ok)]
    [InlineData("normal", NotificationCategory.Normal)]
    [InlineData("attention", NotificationCategory.Attention)]
    public void ParsesExactWireValues(string value, NotificationCategory expected)
    {
        Assert.True(NotificationCategories.TryParse(value, out var category));
        Assert.Equal(expected, category);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OK")]
    [InlineData(" normal")]
    [InlineData("warning")]
    public void RejectsOtherValues(string? value)
        => Assert.False(NotificationCategories.TryParse(value, out _));

    [Fact]
    public void HighestKeepsMostAttentionWorthyUnreadCategory()
    {
        Assert.Equal(NotificationCategory.Normal,
            NotificationCategories.Highest(NotificationCategory.Ok, NotificationCategory.Normal));
        Assert.Equal(NotificationCategory.Attention,
            NotificationCategories.Highest(NotificationCategory.Attention, NotificationCategory.Normal));
    }
}
