namespace UniversalModemManager.Models;

public sealed class SmsConversation
{
    public string Key { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;
    public string LastDate { get; set; } = string.Empty;
    public int UnreadCount { get; set; }
    public List<SmsThreadMessage> Messages { get; } = [];

    public string UnreadDisplay =>
        UnreadCount > 0 ? $"{UnreadCount} unread" : string.Empty;
}

public sealed class SmsThreadMessage
{
    public string Index { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public bool IsIncoming { get; init; }
    public bool IsRead { get; init; }

    // 0 = left/incoming, 2 = right/outgoing in the three-column bubble grid.
    public int BubbleColumn => IsIncoming ? 0 : 2;
    public string DirectionLabel => IsIncoming ? "Received" : "Sent";
}
