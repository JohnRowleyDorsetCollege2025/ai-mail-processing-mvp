using System.Net;
using Aura.WorkerService.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aura.MailIngest.Tests;

public class SampleSubmissionTaskTests
{
    [Fact]
    public async Task Posts_to_generator_submit_endpoint_on_each_interval_and_survives_failures()
    {
        // First call fails, later calls succeed: the task must keep polling after an error.
        var handler = new RecordingHandler(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        var task = CreateTask(handler, new SampleSubmissionOptions
        {
            Interval = TimeSpan.FromSeconds(1),
            BaseUrl = new Uri("http://generator.test"),
            Mailbox = "traded",
        });

        await task.StartAsync(CancellationToken.None);
        await WaitUntil(() => handler.Requests.Count >= 2, TimeSpan.FromSeconds(10));
        await task.StopAsync(CancellationToken.None);

        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("http://generator.test/api/samples/email/submit", r.Uri);
            Assert.Contains("\"mailbox\":\"traded\"", r.Body);
        });
    }

    [Fact]
    public async Task Disabled_task_does_not_call_generator()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var task = CreateTask(handler, new SampleSubmissionOptions { Enabled = false, Interval = TimeSpan.FromSeconds(1) });

        await task.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        await task.StopAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    private static IHostedService CreateTask(RecordingHandler handler, SampleSubmissionOptions options)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(SampleSubmissionTask.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new SampleSubmissionTask(factory, new StaticOptionsMonitor(options), NullLogger<SampleSubmissionTask>.Instance);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the task to call the generator.");
            await Task.Delay(50);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string Body);

    private sealed class RecordingHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body));
            }
            var status = statuses[Math.Min(Requests.Count - 1, statuses.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"ingestStatus":202,"scenario":{"seed":1}}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StaticOptionsMonitor(SampleSubmissionOptions value) : IOptionsMonitor<SampleSubmissionOptions>
    {
        public SampleSubmissionOptions CurrentValue => value;
        public SampleSubmissionOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<SampleSubmissionOptions, string?> listener) => null;
    }
}

public class ForwardPendingTaskTests
{
    [Fact]
    public async Task Forwarding_client_is_not_limited_by_the_shared_resilience_timeout()
    {
        // Shared defaults as in Aura.ServiceDefaults, but with a tiny timeout: if the forwarding client still had the
        // shared handler, a 1s call would be cut off at 200ms. It must succeed, limited only by its own HttpClient.Timeout.
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(200);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMilliseconds(300);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(1);
        }));
        Aura.WorkerService.Tasks.ForwardPendingTask.AddHttpClient(services);
        services.AddHttpClient(Aura.WorkerService.Tasks.ForwardPendingTask.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SlowHandler(TimeSpan.FromSeconds(1)));
        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>()
            .CreateClient(Aura.WorkerService.Tasks.ForwardPendingTask.HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(5); // as ForwardPendingTask does from Tasks:ForwardPending:Timeout

        var response = await client.PostAsync("http://ingest.test/api/submissions/forward", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
