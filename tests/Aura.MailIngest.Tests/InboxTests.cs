using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Aura.Contracts;
using Aura.MailIngest.Api;
using Aura.SampleMailGenerator.Api;
using Aura.SampleMailGenerator.Api.Attachments;
using Aura.SampleMailGenerator.Api.Scenarios;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aura.MailIngest.Tests;

public class InboxTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aura-inbox-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Inbox_lists_submissions_with_body_sender_and_ai_result()
    {
        using var factory = CreateFactory(HttpStatusCode.Accepted);
        using var client = factory.CreateClient();
        var scenario = new ScenarioGenerator(TimeProvider.System).Generate(new ScenarioRequest { Seed = 21 });
        await client.PostAsync("/api/submissions", RawEml(scenario));
        (await client.PostAsync("/api/submissions/forward", null)).EnsureSuccessStatusCode();

        var inbox = await client.GetFromJsonAsync<List<InboxMessage>>("/api/inbox");

        var message = Assert.Single(inbox!);
        Assert.Equal("Processed", message.Status);
        Assert.Equal(scenario.Broker.ContactName, message.SenderName);
        Assert.Equal(scenario.Broker.Email, message.SenderEmail);
        Assert.DoesNotContain("SUBMISSION\n", message.Body);
        Assert.Contains(scenario.Insured.DisplayName, message.Body);
        Assert.Single(message.AttachmentNames);
        Assert.Equal("BB06", message.Ctp!.OccupationCode);
    }

    [Fact]
    public async Task Hub_pushes_pending_on_arrival_then_final_status_when_forwarded()
    {
        using var factory = CreateFactory(HttpStatusCode.BadRequest);
        var updates = Channel.CreateUnbounded<InboxMessage>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/inbox"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        hub.On<InboxMessage>(InboxHubMethods.MessageUpserted, m => updates.Writer.TryWrite(m));
        await hub.StartAsync();

        using var client = factory.CreateClient();
        await client.PostAsync("/api/submissions", RawEml(new ScenarioGenerator(TimeProvider.System).Generate(new ScenarioRequest { Seed = 22 })));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await updates.Reader.ReadAsync(timeout.Token);
        await client.PostAsync("/api/submissions/forward", null);
        var second = await updates.Reader.ReadAsync(timeout.Token);
        Assert.Equal("Pending", first.Status);
        Assert.Equal("Failed", second.Status);
        Assert.Equal(first.Id, second.Id);
        Assert.Contains("400", second.Error);
    }

    [Fact]
    public async Task Inbox_allows_configured_browser_origin_only()
    {
        using var factory = CreateFactory(HttpStatusCode.Accepted);
        using var client = factory.CreateClient();

        async Task<string?> AllowedOriginFor(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/inbox");
            request.Headers.Add("Origin", origin);
            using var response = await client.SendAsync(request);
            return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;
        }

        Assert.Equal("http://localhost:5165", await AllowedOriginFor("http://localhost:5165"));
        Assert.Null(await AllowedOriginFor("http://evil.example"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    private WebApplicationFactory<EmlSubmissionParser> CreateFactory(HttpStatusCode receiverStatus) =>
        new WebApplicationFactory<EmlSubmissionParser>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:DatabasePath", _databasePath);
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<AiReceiverClient>().ConfigurePrimaryHttpMessageHandler(() => new HotelReceiver(receiverStatus)));
        });

    private static ByteArrayContent RawEml(SubmissionScenario scenario)
    {
        var message = new SampleEmailBuilder([new SubmissionPdfGenerator()], TimeProvider.System).Build(scenario);
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        return content;
    }

    private sealed class HotelReceiver(HttpStatusCode status) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var ack = new AiSubmissionAcknowledgement
            {
                SubmissionId = doc.RootElement.GetProperty("context").GetProperty("submissionId").GetString()!,
                Status = "accepted",
                ReceivedAt = DateTimeOffset.UtcNow,
                Ctp = new AiCtpModel { OccupationCode = "BB06", Appetite = "A = Not Declined" },
            };
            return new HttpResponseMessage(status) { Content = JsonContent.Create(ack) };
        }
    }
}
