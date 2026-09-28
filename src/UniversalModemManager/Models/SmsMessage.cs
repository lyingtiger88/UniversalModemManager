namespace UniversalModemManager.Models;

public enum SmsBoxType
{
    Inbox = 1,
    Sent = 2,
    Draft = 3
}

public sealed record SmsMessage(
    string Index,
    string Phone,
    string Content,
    string Date,
    bool IsRead,
    string? SmsType = null,
    string? Sca = null)
{
    public string ReadState => IsRead ? "Read" : "Unread";

    public string Preview =>
        string.IsNullOrWhiteSpace(Content)
            ? string.Empty
            : Content.Replace("\r", " ").Replace("\n", " ");
}

public sealed record SmsCounts(
    int LocalUnread,
    int LocalInbox,
    int LocalOutbox,
    int LocalDraft,
    int SimUnread = 0,
    int SimInbox = 0);
