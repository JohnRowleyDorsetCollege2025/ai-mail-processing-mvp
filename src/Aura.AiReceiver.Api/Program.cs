using System.Collections.Concurrent;
using Aura.Contracts;

// Placeholder for the third-party AI processing API. It validates and acknowledges submissions, returning a
// stand-in AI CTP model from simple keyword matching (see PlaceholderCtpExtractor) in place of real AI processing.
// A short in-memory history is kept so end-to-end tests can confirm what arrived.

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReceivedSubmissionLog>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Payloads carry base64 attachments, so allow well beyond the 30 MB default.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 100 * 1024 * 1024);

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();

app.MapDefaultEndpoints();

app.MapPost("/api/ai/submissions", (
    AiSubmissionPayload payload,
    ReceivedSubmissionLog log,
    TimeProvider time,
    ILogger<Program> logger) =>
{
    var errors = Validate(payload);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var receivedAt = time.GetUtcNow();
    log.Add(new ReceivedSubmission(
        payload.Context.SubmissionId,
        payload.Context.Mailbox,
        payload.Context.ReceivedOn,
        payload.AttachedDocuments.Select(d => new ReceivedDocument(d.FileName, d.MimeType, d.Content.Length * 3 / 4)).ToList(),
        payload.Content,
        receivedAt));

    logger.LogInformation(
        "Received submission {SubmissionId} from mailbox {Mailbox} with {AttachmentCount} attachment(s) for agents {AgentIds}",
        payload.Context.SubmissionId, payload.Context.Mailbox, payload.AttachedDocuments.Count, payload.AgentIds);

    return Results.Accepted(
        $"/api/ai/submissions/{payload.Context.SubmissionId}",
        new AiSubmissionAcknowledgement
        {
            SubmissionId = payload.Context.SubmissionId,
            Status = "accepted",
            ReceivedAt = receivedAt,
            Ctp = PlaceholderCtpExtractor.Extract(payload),
        });
})
.WithSummary("Accept a submission for AI processing (placeholder: returns a keyword-based CTP model)");

app.MapGet("/api/ai/submissions", (ReceivedSubmissionLog log) => Results.Ok(log.Recent()))
    .WithSummary("List recently received submissions (in-memory, most recent first)");

app.MapGet("/api/ai/submissions/{submissionId}", (string submissionId, ReceivedSubmissionLog log) =>
    log.TryGet(submissionId, out var submission) ? Results.Ok(submission) : Results.NotFound())
    .WithSummary("Get a received submission by id");

app.Run();

static Dictionary<string, string[]> Validate(AiSubmissionPayload payload)
{
    var errors = new Dictionary<string, string[]>();

    if (payload.AgentIds is null || payload.AgentIds.Count == 0)
    {
        errors["agent_ids"] = ["At least one agent id is required."];
    }
    if (string.IsNullOrWhiteSpace(payload.Content))
    {
        errors["content"] = ["Content is required."];
    }
    if (payload.Context is null || string.IsNullOrWhiteSpace(payload.Context.SubmissionId))
    {
        errors["context.submissionId"] = ["A submission id is required."];
    }
    else if (payload.Context.AttachmentCount != payload.AttachedDocuments?.Count)
    {
        errors["context.attachmentCount"] = ["attachmentCount does not match the number of attached_documents."];
    }

    return errors;
}

public sealed record ReceivedDocument(string FileName, string MimeType, long ApproximateSizeBytes);

public sealed record ReceivedSubmission(
    string SubmissionId,
    string Mailbox,
    string ReceivedOn,
    IReadOnlyList<ReceivedDocument> Documents,
    string Content,
    DateTimeOffset AcceptedAt);

public sealed class ReceivedSubmissionLog
{
    private const int Capacity = 100;
    private readonly ConcurrentDictionary<string, ReceivedSubmission> _byId = new();
    private readonly ConcurrentQueue<string> _order = new();

    public void Add(ReceivedSubmission submission)
    {
        _byId[submission.SubmissionId] = submission;
        _order.Enqueue(submission.SubmissionId);
        while (_order.Count > Capacity && _order.TryDequeue(out var oldest))
        {
            _byId.TryRemove(oldest, out _);
        }
    }

    public bool TryGet(string submissionId, out ReceivedSubmission? submission) =>
        _byId.TryGetValue(submissionId, out submission);

    public IReadOnlyList<ReceivedSubmission> Recent() =>
        _byId.Values.OrderByDescending(s => s.AcceptedAt).ToList();
}
