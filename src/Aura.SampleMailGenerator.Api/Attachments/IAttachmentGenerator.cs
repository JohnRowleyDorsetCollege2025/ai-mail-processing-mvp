using Aura.SampleMailGenerator.Api.Scenarios;

namespace Aura.SampleMailGenerator.Api.Attachments;

public sealed record GeneratedAttachment(string FileName, string MimeType, byte[] Content);

/// <summary>
/// Renders one attachment from a scenario. Add implementations (Word proposal form, claims schedule spreadsheet,
/// scanned-image PDF, ...) and register them in Program.cs to widen the variety of generated submissions.
/// </summary>
public interface IAttachmentGenerator
{
    /// <summary>Stable key used to select generators via the API, e.g. "submission-pdf".</summary>
    string Key { get; }

    GeneratedAttachment Generate(SubmissionScenario scenario);
}
