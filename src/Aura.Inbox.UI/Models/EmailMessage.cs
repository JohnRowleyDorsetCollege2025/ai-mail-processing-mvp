using Aura.Contracts;

namespace InboxMock.Models;

public enum ProcessingStatus
{
    Pending,
    Processed,
    Failed
}

public class EmailMessage
{
    public string Id { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public ProcessingStatus Status { get; set; }
    public string ReceivedOn { get; set; } = "";
    public IReadOnlyList<string> AttachmentNames { get; set; } = [];
    public string? Error { get; set; }
    public AiCtpModel? Ctp { get; set; }

    public static EmailMessage From(InboxMessage m) => new()
    {
        Id = m.Id,
        Subject = m.Subject,
        Body = m.Body,
        SenderName = m.SenderName,
        SenderEmail = m.SenderEmail,
        Status = Enum.TryParse<ProcessingStatus>(m.Status, ignoreCase: true, out var status) ? status : ProcessingStatus.Pending,
        ReceivedOn = m.ReceivedOn,
        AttachmentNames = m.AttachmentNames,
        Error = m.Error,
        Ctp = m.Ctp,
    };
}
