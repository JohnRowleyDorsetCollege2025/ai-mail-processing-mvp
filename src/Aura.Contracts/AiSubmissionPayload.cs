using System.Text.Json.Serialization;

namespace Aura.Contracts;

/// <summary>
/// The payload submitted to the AI processing API. Property names mirror data/sample_ai_payload.json exactly,
/// so do not rename them without agreeing the change with the vendor.
/// </summary>
public sealed record AiSubmissionPayload
{
    [JsonPropertyName("agent_ids")]
    public required IReadOnlyList<string> AgentIds { get; init; }

    /// <summary>Plain-text rendering of the submission header and email body, intended for the model.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("context")]
    public required SubmissionContext Context { get; init; }

    [JsonPropertyName("attached_documents")]
    public required IReadOnlyList<AttachedDocument> AttachedDocuments { get; init; }

    [JsonPropertyName("async_processing")]
    public bool AsyncProcessing { get; init; } = true;
}

public sealed record SubmissionContext
{
    [JsonPropertyName("submissionId")]
    public required string SubmissionId { get; init; }

    [JsonPropertyName("mailbox")]
    public required string Mailbox { get; init; }

    /// <summary>UTC timestamp in ISO-8601 form, e.g. 2026-10-13T12:09:00Z.</summary>
    [JsonPropertyName("receivedOn")]
    public required string ReceivedOn { get; init; }

    [JsonPropertyName("attachmentCount")]
    public required int AttachmentCount { get; init; }
}

public sealed record AttachedDocument
{
    [JsonPropertyName("file_name")]
    public required string FileName { get; init; }

    [JsonPropertyName("mime_type")]
    public required string MimeType { get; init; }

    /// <summary>Base64-encoded file content.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}
