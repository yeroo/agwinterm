namespace Agwinterm.Pty;

/// <summary>Priority of an unread notification; the highest category determines the badge color.</summary>
public enum NotificationCategory
{
    Ok,
    Normal,
    Attention,
}

public static class NotificationCategories
{
    public static bool TryParse(string? value, out NotificationCategory category)
    {
        category = value switch
        {
            "ok" => NotificationCategory.Ok,
            "normal" => NotificationCategory.Normal,
            "attention" => NotificationCategory.Attention,
            _ => NotificationCategory.Attention,
        };
        return value is "ok" or "normal" or "attention";
    }

    public static NotificationCategory Highest(NotificationCategory first, NotificationCategory second)
        => (int)first >= (int)second ? first : second;
}
