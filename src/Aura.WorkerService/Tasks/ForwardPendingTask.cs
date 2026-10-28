using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Aura.WorkerService.Tasks;

public sealed class ForwardPendingOptions : PeriodicTaskOptions
{
    public const string SectionName = "Tasks:ForwardPending";

    public ForwardPendingOptions()
    {
        Interval = TimeSpan.FromMinutes(2);
    }

    /// <summary>Base URL of Aura.MailIngest.Api. Aspire overrides this with the ingest API's endpoint.</summary>
    public Uri BaseUrl { get; set; } = new("http://localhost:5100");

    /// <summary>Throttle: most submissions sent to the AI per run (the ingest API also caps this at Forwarding:MaxBatchSize).</summary>
    public int MaxPerRun { get; set; } = 5;

    /// <summary>
    /// How long one run may take. The ingest API waits for each AI response before sending the next, so allow for
    /// MaxPerRun slow AI responses. If exceeded, ingest finishes the submission in flight and stops.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Task 2: asks the mail ingest API to send its pending submissions to the AI, a few at a time. Throttling and the
/// one-at-a-time sending happen in the ingest API (POST /api/submissions/forward); this task only sets the pace.
/// </summary>
public sealed class ForwardPendingTask(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ForwardPendingOptions> options,
    ILogger<ForwardPendingTask> logger)
    : PeriodicTask<ForwardPendingOptions>(options, logger)
{
    public const string HttpClientName = "MailIngestForwarding";

    /// <summary>
    /// Registers the HTTP client. One run waits for several AI responses, far longer than the shared resilience
    /// handler allows (Aura.ServiceDefaults: 100s). The shared handler's settings are common to every client, so
    /// they can't be raised for this one; instead the handler is removed and HttpClient.Timeout enforces
    /// Tasks:ForwardPending:Timeout. The call is never retried (a retry could re-send to the AI).
    /// </summary>
    public static void AddHttpClient(IServiceCollection services)
    {
        // EXTEXP0001: RemoveAllResilienceHandlers is marked experimental; it is the documented way to opt one client
        // out of handlers added by ConfigureHttpClientDefaults. If it changes, revisit this registration.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(HttpClientName).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    protected override async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = Options.Timeout;
        var uri = new Uri(Options.BaseUrl, $"/api/submissions/forward?max={Options.MaxPerRun}");

        using var response = await client.PostAsync(uri, content: null, stoppingToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            logger.LogInformation("Skipped: a forwarding run is already in progress in the ingest API");
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Forwarding run failed: ingest returned {StatusCode}: {Body}",
                (int)response.StatusCode, await response.Content.ReadAsStringAsync(stoppingToken));
            return;
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(stoppingToken);
        var accepted = result.GetProperty("accepted").GetInt32();
        var failed = result.GetProperty("failed").GetInt32();
        var retry = result.GetProperty("retry").GetInt32();
        var remaining = result.GetProperty("remainingPending").GetInt32();

        if (accepted + failed + retry == 0)
        {
            logger.LogDebug("Nothing pending to forward");
            return;
        }

        logger.Log(
            retry > 0 ? LogLevel.Warning : LogLevel.Information,
            "Forwarded to AI: {Accepted} accepted, {Failed} rejected, {Retry} to retry; {Remaining} still pending",
            accepted, failed, retry, remaining);
    }
}
