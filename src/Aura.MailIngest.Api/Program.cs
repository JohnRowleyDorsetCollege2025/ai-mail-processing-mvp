using Aura.MailIngest.Api;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var ingest = context.Configuration.GetSection(IngestOptions.SectionName).Get<IngestOptions>() ?? new IngestOptions();
    kestrel.Limits.MaxRequestBodySize = ingest.MaxEmlSizeBytes;
});

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var submissions = app.MapGroup("/api/submissions");

// Parses the .eml and forwards the payload to the AI receiver.
submissions.MapPost("/", async (
    HttpRequest request,
    string? mailbox,
    EmlSubmissionParser parser,
    AiReceiverClient receiver,
    IOptions<IngestOptions> options,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var (payload, error) = await ParseRequestAsync(request, mailbox, parser, options.Value, ct);
    if (error is not null)
    {
        return error;
    }

    logger.LogInformation(
        "Parsed submission {SubmissionId} with {AttachmentCount} attachment(s); forwarding to AI receiver",
        payload!.Context.SubmissionId, payload.Context.AttachmentCount);

    try
    {
        var ack = await receiver.SubmitAsync(payload, ct);
        return Results.Accepted(value: new
        {
            submissionId = payload.Context.SubmissionId,
            attachmentCount = payload.Context.AttachmentCount,
            receiver = ack,
        });
    }
    catch (Exception ex) when (ex is AiReceiverException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
    {
        logger.LogError(ex, "Failed to forward submission {SubmissionId}", payload.Context.SubmissionId);
        return Results.Problem(
            title: "AI receiver unavailable",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
})
.DisableAntiforgery()
.WithSummary("Parse an .eml file and submit it for AI processing")
.WithDescription("Send the .eml either as multipart/form-data (field 'file') or as a raw message/rfc822 body.");

// Returns the payload that would be sent, without forwarding it. Useful for inspecting the parser output.
submissions.MapPost("/preview", async (
    HttpRequest request,
    string? mailbox,
    EmlSubmissionParser parser,
    IOptions<IngestOptions> options,
    CancellationToken ct) =>
{
    var (payload, error) = await ParseRequestAsync(request, mailbox, parser, options.Value, ct);
    return error ?? Results.Ok(payload);
})
.DisableAntiforgery()
.WithSummary("Parse an .eml file and return the AI payload without submitting it");

app.Run();

static async Task<(Aura.Contracts.AiSubmissionPayload? Payload, IResult? Error)> ParseRequestAsync(
    HttpRequest request,
    string? mailbox,
    EmlSubmissionParser parser,
    IngestOptions options,
    CancellationToken ct)
{
    Stream eml;
    if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return (null, Results.Problem("Upload the .eml file in a form field named 'file'.", statusCode: StatusCodes.Status400BadRequest));
        }

        mailbox ??= form["mailbox"].FirstOrDefault();
        eml = file.OpenReadStream();
    }
    else
    {
        // MimeKit's parser needs a seekable stream for reliable header/body parsing, so buffer the raw body.
        var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            return (null, Results.Problem("Request body is empty. Send an .eml as multipart/form-data or message/rfc822.", statusCode: StatusCodes.Status400BadRequest));
        }

        buffer.Position = 0;
        eml = buffer;
    }

    await using (eml)
    {
        try
        {
            var payload = await parser.ParseAsync(
                eml,
                string.IsNullOrWhiteSpace(mailbox) ? options.DefaultMailbox : mailbox.Trim(),
                options.AgentIds,
                ct);
            return (payload, null);
        }
        catch (InvalidEmlException ex)
        {
            return (null, Results.Problem(ex.Message, title: "Invalid .eml file", statusCode: StatusCodes.Status400BadRequest));
        }
    }
}
