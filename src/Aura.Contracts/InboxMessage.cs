using System.Text.Json.Serialization;

namespace Aura.Contracts;

/// <summary>
/// An email as shown in the inbox UI. Returned by the mail ingest API's <c>GET /api/inbox</c> and pushed over the
/// SignalR hub at <c>/hubs/inbox</c> (method <see cref="InboxHubMethods.MessageUpserted"/>) whenever a submission
/// arrives or its status changes.
/// </summary>
public sealed record InboxMessage
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    /// <summary>The email body text (without the submission header block sent to the AI).</summary>
    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("senderName")]
    public required string SenderName { get; init; }

    [JsonPropertyName("senderEmail")]
    public required string SenderEmail { get; init; }

    /// <summary>"Pending" (stored, being sent to the AI), "Processed" (accepted by the AI) or "Failed".</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("receivedOn")]
    public required string ReceivedOn { get; init; }

    [JsonPropertyName("attachmentNames")]
    public required IReadOnlyList<string> AttachmentNames { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>The AI's extraction, once processed.</summary>
    [JsonPropertyName("ctp")]
    public AiCtpModel? Ctp { get; init; }
}

public static class InboxHubMethods
{
    /// <summary>Server-to-client: a message was added or changed. Argument: <see cref="InboxMessage"/>.</summary>
    public const string MessageUpserted = "MessageUpserted";
}
