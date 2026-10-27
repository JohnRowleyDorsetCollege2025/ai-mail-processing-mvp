using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Aura.WorkerService.Tasks;

public sealed class SampleSubmissionOptions : PeriodicTaskOptions
{
    public const string SectionName = "Tasks:SampleSubmission";

    /// <summary>Base URL of Aura.SampleMailGenerator.Api. Aspire overrides this with the generator's endpoint.</summary>
    public Uri BaseUrl { get; set; } = new("http://localhost:5300");

    /// <summary>Optional mailbox passed through to the ingest API; the ingest default is used when empty.</summary>
    public string? Mailbox { get; set; }
}

/// <summary>
/// Task 1: asks the sample mail generator to create a random broker submission and push it through
/// ingest to the AI receiver, giving a steady stream of test traffic.
/// </summary>
public sealed class SampleSubmissionTask(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<SampleSubmissionOptions> options,
    ILogger<SampleSubmissionTask> logger)
    : PeriodicTask<SampleSubmissionOptions>(options, logger)
{
    public const string HttpClientName = "SampleMailGenerator";
    private const string SubmitPath = "/api/samples/email/submit";

    protected override async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var request = new { mailbox = Options.Mailbox };

        using var response = await client.PostAsJsonAsync(new Uri(Options.BaseUrl, SubmitPath), request, stoppingToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(stoppingToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Sample submission failed: generator returned {StatusCode}: {Body}",
                (int)response.StatusCode, body.ToString());
            return;
        }

        logger.LogInformation(
            "Submitted sample {SubmissionId} for {Insured} (seed {Seed}); ingest returned {IngestStatus}",
            Read(body, "ingestResponse", "submissionId"),
            Read(body, "scenario", "insured", "legalName"),
            Read(body, "scenario", "seed"),
            Read(body, "ingestStatus"));
    }

    private static string? Read(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }
        return element.ToString();
    }
}
