using Aura.MailIngest.Api;
using Aura.MailIngest.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddOptions<IngestOptions>().BindConfiguration(IngestOptions.SectionName);
builder.Services.AddOptions<AiReceiverOptions>().BindConfiguration(AiReceiverOptions.SectionName).ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EmlSubmissionParser>();
builder.Services.AddHttpClient<AiReceiverClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<AiReceiverOptions>>().Value;
    client.BaseAddress = options.BaseUrl;
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

// Dev/demo storage of received emails. The path is relative to the project folder; delete the file to start afresh.
var databasePath = Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["Storage:DatabasePath"] ?? "App_Data/submissions.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
builder.Services.AddDbContext<SubmissionDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<SubmissionStore>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var ingest = context.Configuration.GetSection(IngestOptions.SectionName).Get<IngestOptions>() ?? new IngestOptions();
    kestrel.Limits.MaxRequestBodySize = ingest.MaxEmlSizeBytes;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<SubmissionDbContext>().EnsureSchema();
}

app.UseExceptionHandler();
app.MapOpenApi();

app.MapDefaultEndpoints();

var submissions = app.MapGroup("/api/submissions");

// Parses the .eml, stores it, and forwards the payload to the AI receiver.
submissions.MapPost("/", async (
    HttpRequest request,
    string? mailbox,
    EmlSubmissionParser parser,
    AiReceiverClient receiver,
    SubmissionStore store,
    IOptions<IngestOptions> options,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var (parsed, rawEml, error) = await ParseRequestAsync(request, mailbox, parser, options.Value, ct);
    if (error is not null)
    {
        return error;
    }

    var payload = parsed!.Payload;
    var stored = await store.AddAsync(parsed, rawEml!, ct);

    logger.LogInformation(
        "Parsed submission {SubmissionId} with {AttachmentCount} attachment(s); forwarding to AI receiver",
        payload.Context.SubmissionId, payload.Context.AttachmentCount);

    try
    {
        var ack = await receiver.SubmitAsync(payload, ct);
        await store.MarkForwardedAsync(stored, ack, ct);
        return Results.Accepted($"/api/submissions/{payload.Context.SubmissionId}", new
        {
            submissionId = payload.Context.SubmissionId,
            attachmentCount = payload.Context.AttachmentCount,
            receiver = ack,
        });
    }
    catch (Exception ex) when (ex is AiReceiverException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
    {
        logger.LogError(ex, "Failed to forward submission {SubmissionId}", payload.Context.SubmissionId);
        await store.MarkFailedAsync(stored, ex.Message, ct);
        return Results.Problem(
            title: "AI receiver unavailable",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
})
.DisableAntiforgery()
.WithSummary("Parse an .eml file and submit it for AI processing")
.WithDescription("Send the .eml either as multipart/form-data (field 'file') or as a raw message/rfc822 body.");

// Returns the payload that would be sent, without storing or forwarding it. Useful for inspecting the parser output.
submissions.MapPost("/preview", async (
    HttpRequest request,
    string? mailbox,
    EmlSubmissionParser parser,
    IOptions<IngestOptions> options,
    CancellationToken ct) =>
{
    var (parsed, _, error) = await ParseRequestAsync(request, mailbox, parser, options.Value, ct);
    return error ?? Results.Ok(parsed!.Payload);
})
.DisableAntiforgery()
.WithSummary("Parse an .eml file and return the AI payload without submitting it");

submissions.MapGet("/", async (SubmissionStatus? status, int? take, SubmissionStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(status, Math.Clamp(take ?? 50, 1, 500), ct)))
.WithSummary("List stored submissions, most recent first (optionally filter by status: Pending, Accepted, Failed)");

submissions.MapGet("/{submissionId}", async (string submissionId, SubmissionStore store, CancellationToken ct) =>
    await store.GetAsync(submissionId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound())
.WithSummary("Get a stored submission, including the content text sent to the AI and the AI response");

submissions.MapGet("/{submissionId}/eml", async (string submissionId, SubmissionStore store, CancellationToken ct) =>
    await store.GetRawEmlAsync(submissionId, ct) is { } eml
        ? Results.File(eml, "message/rfc822", $"{submissionId}.eml")
        : Results.NotFound())
.WithSummary("Download the original .eml exactly as received");

app.Run();

static async Task<(ParsedSubmission? Parsed, byte[]? RawEml, IResult? Error)> ParseRequestAsync(
    HttpRequest request,
    string? mailbox,
    EmlSubmissionParser parser,
    IngestOptions options,
    CancellationToken ct)
{
    // Buffer the whole .eml: MimeKit parses more reliably from a seekable stream, and the raw bytes are stored.
    using var buffer = new MemoryStream();
    if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return (null, null, Results.Problem("Upload the .eml file in a form field named 'file'.", statusCode: StatusCodes.Status400BadRequest));
        }

        mailbox ??= form["mailbox"].FirstOrDefault();
        await file.CopyToAsync(buffer, ct);
    }
    else
    {
        await request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            return (null, null, Results.Problem("Request body is empty. Send an .eml as multipart/form-data or message/rfc822.", statusCode: StatusCodes.Status400BadRequest));
        }
    }

    buffer.Position = 0;
    try
    {
        var parsed = await parser.ParseAsync(
            buffer,
            string.IsNullOrWhiteSpace(mailbox) ? options.DefaultMailbox : mailbox.Trim(),
            options.AgentIds,
            ct);
        return (parsed, buffer.ToArray(), null);
    }
    catch (InvalidEmlException ex)
    {
        return (null, null, Results.Problem(ex.Message, title: "Invalid .eml file", statusCode: StatusCodes.Status400BadRequest));
    }
}
