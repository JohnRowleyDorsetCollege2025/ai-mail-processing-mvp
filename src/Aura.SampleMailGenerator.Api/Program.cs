using Aura.SampleMailGenerator.Api;
using Aura.SampleMailGenerator.Api.Attachments;
using Aura.SampleMailGenerator.Api.Scenarios;
using MimeKit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ScenarioGenerator>();
builder.Services.AddSingleton<IAttachmentGenerator, SubmissionPdfGenerator>();
builder.Services.AddSingleton<SampleEmailBuilder>();
builder.Services.AddHttpClient("MailIngest", (sp, client) =>
{
    client.BaseAddress = new Uri(sp.GetRequiredService<IConfiguration>()["MailIngest:BaseUrl"]
        ?? throw new InvalidOperationException("MailIngest:BaseUrl is not configured."));
});
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var samples = app.MapGroup("/api/samples");

samples.MapGet("/options", (SampleEmailBuilder emails) => Results.Ok(new
{
    trades = ScenarioGenerator.Trades,
    requestTypes = new[] { "New Business", "Renewal" },
    attachments = emails.AttachmentKeys,
}))
.WithSummary("List the values accepted by the generation endpoints");

samples.MapPost("/scenario", (SampleRequest? request, ScenarioGenerator scenarios) =>
    Generate(() => Results.Ok(scenarios.Generate(request?.Scenario))))
.WithSummary("Generate the ground-truth scenario only (same seed gives the same facts as the email)");

// Downloads a generated .eml. The scenario seed is returned in X-Aura-Sample-Seed so the facts can be regenerated.
samples.MapPost("/email", (SampleRequest? request, ScenarioGenerator scenarios, SampleEmailBuilder emails) =>
    Generate(() =>
    {
        var scenario = scenarios.Generate(request?.Scenario);
        var message = emails.Build(scenario, request?.Attachments);
        return new EmlResult(message, scenario.Seed);
    }))
.WithSummary("Generate a sample broker submission email (.eml)");

samples.MapGet("/email", (int? seed, ScenarioGenerator scenarios, SampleEmailBuilder emails) =>
    Generate(() =>
    {
        var scenario = scenarios.Generate(new ScenarioRequest { Seed = seed });
        return new EmlResult(emails.Build(scenario), scenario.Seed);
    }))
.WithSummary("Generate a sample broker submission email (.eml) with default options");

// Generates an email and posts it straight to the mail ingest API, for end-to-end testing.
samples.MapPost("/email/submit", async (
    SampleRequest? request,
    ScenarioGenerator scenarios,
    SampleEmailBuilder emails,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct) =>
{
    SubmissionScenario scenario;
    MimeMessage message;
    try
    {
        scenario = scenarios.Generate(request?.Scenario);
        message = emails.Build(scenario, request?.Attachments);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }

    using var eml = new MemoryStream();
    await message.WriteToAsync(eml, ct);
    eml.Position = 0;

    using var content = new StreamContent(eml);
    content.Headers.ContentType = new("message/rfc822");
    var client = httpClientFactory.CreateClient("MailIngest");
    var path = string.IsNullOrWhiteSpace(request?.Mailbox) ? "/api/submissions" : $"/api/submissions?mailbox={Uri.EscapeDataString(request.Mailbox)}";

    try
    {
        using var response = await client.PostAsync(path, content, ct);
        var ingestBody = await response.Content.ReadAsStringAsync(ct);
        return Results.Json(new
        {
            ingestStatus = (int)response.StatusCode,
            ingestResponse = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(ingestBody) ? "null" : ingestBody).RootElement,
            scenario,
        }, statusCode: response.IsSuccessStatusCode ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(ex.Message, title: "Mail ingest API unavailable", statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithSummary("Generate a sample email and submit it to the mail ingest API");

app.Run();

static IResult Generate(Func<IResult> action)
{
    try
    {
        return action();
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record SampleRequest
{
    public ScenarioRequest? Scenario { get; init; }

    /// <summary>Attachment generator keys, e.g. ["submission-pdf"]. Defaults to the submission PDF.</summary>
    public IReadOnlyList<string>? Attachments { get; init; }

    /// <summary>Mailbox passed to the ingest API by the submit endpoint.</summary>
    public string? Mailbox { get; init; }
}

internal sealed class EmlResult(MimeMessage message, int seed) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var fileName = $"sample-submission-{seed}.eml";
        httpContext.Response.ContentType = "message/rfc822";
        httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        httpContext.Response.Headers["X-Aura-Sample-Seed"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await message.WriteToAsync(httpContext.Response.Body, httpContext.RequestAborted);
    }
}
