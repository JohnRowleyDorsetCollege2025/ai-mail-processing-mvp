namespace Aura.MailIngest.Api;

public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>AURA agent ids written to <c>agent_ids</c> on every payload.</summary>
    public List<string> AgentIds { get; set; } = [];

    /// <summary>Mailbox used when the caller does not supply one.</summary>
    public string DefaultMailbox { get; set; } = "traded";

    /// <summary>Upper bound on the uploaded .eml size. Attachments are base64-encoded into the payload, so this also caps payload size.</summary>
    public long MaxEmlSizeBytes { get; set; } = 50 * 1024 * 1024;
}

public sealed class AiReceiverOptions
{
    public const string SectionName = "AiReceiver";

    public required Uri BaseUrl { get; set; }

    public string SubmissionsPath { get; set; } = "/api/ai/submissions";

    public int TimeoutSeconds { get; set; } = 100;
}
