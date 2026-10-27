using System.Text.Json.Serialization;

namespace InboxMock.Models;

public enum ProcessingStatus
{
    Pending,
    Processed,
    Failed
}

public class EmailMessage
{
    public Guid Id { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string SenderEmail { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessingStatus Status { get; set; }
}
