using System.Text.Json.Serialization;

namespace Aura.Contracts;

/// <summary>Response returned by the AI receiver once a submission has been accepted for processing.</summary>
public sealed record AiSubmissionAcknowledgement
{
    [JsonPropertyName("submissionId")]
    public required string SubmissionId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("receivedAt")]
    public required DateTimeOffset ReceivedAt { get; init; }
}
