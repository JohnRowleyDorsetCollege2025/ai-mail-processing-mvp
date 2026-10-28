using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aura.Contracts;
using Aura.MailIngest.Api;
using Aura.MailIngest.Api.Forwarding;
using Aura.MailIngest.Api.Storage;
using Aura.SampleMailGenerator.Api;
using Aura.SampleMailGenerator.Api.Attachments;
using Aura.SampleMailGenerator.Api.Scenarios;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aura.MailIngest.Tests;

public class MailIngestApiTests : IDisposable
{
    // Each test class instance gets its own throwaway database.
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aura-ingest-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Upload_is_stored_as_pending_and_not_sent_to_the_ai()
    {
        var receiver = new StubReceiver(HttpStatusCode.Accepted);
        using var client = CreateClient(receiver);

        var response = await client.PostAsync("/api/submissions", RawEml(GenerateEml()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Pending", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Empty(receiver.Bodies);
        Assert.Equal(SubmissionStatus.Pending, Assert.Single((await ListAsync(client))).Status);
    }

    [Fact]
    public async Task Forward_sends_the_payload_in_the_ai_wire_format()
    {
        var receiver = new StubReceiver(HttpStatusCode.Accepted);
        using var client = CreateClient(receiver);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(GenerateEml());
        file.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        form.Add(file, "file", "submission.eml");
        form.Add(new StringContent("property"), "mailbox");
        await client.PostAsync("/api/submissions", form);

        var run = await ForwardAsync(client);

        Assert.Equal(1, run.Accepted);
        // Assert on the raw JSON so the wire format matches data/sample_ai_payload.json, not just our C# model.
        using var json = JsonDocument.Parse(Assert.Single(receiver.Bodies));
        var root = json.RootElement;
        Assert.Equal(["agent_ids", "content", "context", "attached_documents", "async_processing"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["submissionId", "mailbox", "receivedOn", "attachmentCount"],
            root.GetProperty("context").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["file_name", "mime_type", "content"],
            root.GetProperty("attached_documents")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("property", root.GetProperty("context").GetProperty("mailbox").GetString());
    }

    [Fact]
    public async Task Forwarded_payload_keeps_the_id_and_content_from_arrival()
    {
        var receiver = new StubReceiver(HttpStatusCode.Accepted);
        using var client = CreateClient(receiver);
        var preview = await (await client.PostAsync("/api/submissions/preview", RawEml(GenerateEml()))).Content.ReadFromJsonAsync<AiSubmissionPayload>();
        var submissionId = await UploadAsync(client, GenerateEml());

        await ForwardAsync(client);

        var sent = JsonSerializer.Deserialize<AiSubmissionPayload>(Assert.Single(receiver.Bodies))!;
        var stored = (await client.GetFromJsonAsync<StoredSubmissionDetail>($"/api/submissions/{submissionId}", JsonOptions))!;
        Assert.Equal(submissionId, sent.Context.SubmissionId);
        Assert.Equal(stored.Content, sent.Content);
        Assert.Equal(preview!.AttachedDocuments.Select(d => d.Content), sent.AttachedDocuments.Select(d => d.Content));
    }

    [Fact]
    public async Task Forward_sends_oldest_first_and_at_most_max_per_run()
    {
        var receiver = new StubReceiver(HttpStatusCode.Accepted);
        using var client = CreateClient(receiver);
        var ids = new List<string>();
        for (var seed = 1; seed <= 3; seed++)
        {
            ids.Add(await UploadAsync(client, GenerateEml(seed)));
        }

        var first = await ForwardAsync(client, max: 2);
        var second = await ForwardAsync(client, max: 2);
        var third = await ForwardAsync(client, max: 2);

        Assert.Equal((2, 1), (first.Accepted, first.RemainingPending));
        Assert.Equal((1, 0), (second.Accepted, second.RemainingPending));
        Assert.Empty(third.Results);
        Assert.Equal(ids, receiver.Bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("context").GetProperty("submissionId").GetString()));
    }

    [Fact]
    public async Task Server_error_keeps_submission_pending_and_stops_the_run()
    {
        var receiver = new StubReceiver(HttpStatusCode.InternalServerError);
        using var client = CreateClient(receiver);
        await UploadAsync(client, GenerateEml(1));
        await UploadAsync(client, GenerateEml(2));

        var run = await ForwardAsync(client);

        Assert.Equal((0, 0, 1, 2, true), (run.Accepted, run.Failed, run.Retry, run.RemainingPending, run.StoppedEarly));
        Assert.Single(receiver.Bodies); // the second was not attempted
        var pending = await ListAsync(client, "?status=Pending");
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, s => s.Error?.Contains("500") == true);
    }

    [Fact]
    public async Task Rejected_submission_is_failed_and_the_run_continues()
    {
        var receiver = new StubReceiver(HttpStatusCode.BadRequest);
        using var client = CreateClient(receiver);
        await UploadAsync(client, GenerateEml(1));
        await UploadAsync(client, GenerateEml(2));

        var run = await ForwardAsync(client);

        Assert.Equal((2, 0, 0), (run.Failed, run.Retry, run.RemainingPending));
        var failed = await ListAsync(client, "?status=Failed");
        Assert.All(failed, s => Assert.Contains("400", s.Error));
        var detail = await client.GetFromJsonAsync<StoredSubmissionDetail>($"/api/submissions/{failed[0].SubmissionId}", JsonOptions);
        Assert.Null(detail!.AiResponse);
    }

    [Fact]
    public async Task Concurrent_forward_run_is_refused()
    {
        var receiver = new StubReceiver(HttpStatusCode.Accepted) { Hold = new TaskCompletionSource() };
        using var client = CreateClient(receiver);
        await UploadAsync(client, GenerateEml());

        var firstRun = client.PostAsync("/api/submissions/forward", null);
        await receiver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await client.PostAsync("/api/submissions/forward", null);
        receiver.Hold.SetResult();
        var first = await firstRun;

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Single(receiver.Bodies);
    }

    [Fact]
    public async Task Raw_rfc822_body_is_accepted_by_preview_without_storing()
    {
        var receiver = new StubReceiver(HttpStatusCode.Accepted);
        using var client = CreateClient(receiver);

        var response = await client.PostAsync("/api/submissions/preview", RawEml(GenerateEml()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<AiSubmissionPayload>();
        Assert.Equal("traded", payload!.Context.Mailbox);
        Assert.Equal(1, payload.Context.AttachmentCount);
        Assert.Empty(receiver.Bodies);
    }

    [Fact]
    public async Task Empty_body_returns_bad_request()
    {
        using var client = CreateClient(new StubReceiver(HttpStatusCode.Accepted));

        var response = await client.PostAsync("/api/submissions", new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Forwarded_submission_stores_ai_response_and_original_eml_is_downloadable()
    {
        using var client = CreateClient(new StubReceiver(HttpStatusCode.Accepted));
        var eml = GenerateEml();
        var submissionId = await UploadAsync(client, eml);

        await ForwardAsync(client);

        var stored = Assert.Single(await ListAsync(client));
        Assert.Equal(submissionId, stored.SubmissionId);
        Assert.Equal(SubmissionStatus.Accepted, stored.Status);
        Assert.NotNull(stored.ForwardedAtUtc);
        Assert.StartsWith("Submission - ", stored.Subject);
        Assert.EndsWith(".pdf", stored.AttachmentNames);

        var detail = await client.GetFromJsonAsync<StoredSubmissionDetail>($"/api/submissions/{submissionId}", JsonOptions);
        Assert.StartsWith("SUBMISSION\n", detail!.Content);
        Assert.Equal(submissionId, detail.AiResponse!.SubmissionId);
        var ctp = detail.AiResponse.Ctp!;
        Assert.Equal(("Test Broker", "DN0000001", "BB06", "A = Not Declined"), (ctp.BrokerName, ctp.AgencyCode, ctp.OccupationCode, ctp.Appetite));
        Assert.Equal(["EL", "PL"], ctp.Covers);

        Assert.Equal(eml, await client.GetByteArrayAsync($"/api/submissions/{submissionId}/eml"));
    }

    [Fact]
    public async Task Preview_is_not_stored_and_unknown_id_returns_not_found()
    {
        using var client = CreateClient(new StubReceiver(HttpStatusCode.Accepted));
        await client.PostAsync("/api/submissions/preview", RawEml(GenerateEml()));

        Assert.Empty(await ListAsync(client));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/submissions/does-not-exist")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/submissions/does-not-exist/eml")).StatusCode);
    }

    [Fact]
    public async Task Database_created_before_AIResponse_is_upgraded_without_losing_rows()
    {
        // The table as it was before the AIResponse column existed.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Submissions (
                    SubmissionId TEXT NOT NULL PRIMARY KEY, Mailbox TEXT NOT NULL, "From" TEXT NOT NULL, Subject TEXT NOT NULL,
                    ReceivedOn TEXT NOT NULL, AttachmentCount INTEGER NOT NULL, AttachmentNames TEXT NOT NULL, Content TEXT NOT NULL,
                    RawEml BLOB NOT NULL, StoredAtUtc TEXT NOT NULL, Status TEXT NOT NULL, ForwardedAtUtc TEXT NULL, Error TEXT NULL);
                INSERT INTO Submissions VALUES ('old-1', 'traded', 'a@b.example', 'Old', '2026-10-01T00:00:00Z', 0, '', 'x',
                    X'00', '2026-10-01 00:00:00', 'Accepted', NULL, NULL);
                """;
            command.ExecuteNonQuery();
        }

        using var client = CreateClient(new StubReceiver(HttpStatusCode.Accepted));
        var newId = await UploadAsync(client, GenerateEml());
        await ForwardAsync(client);

        Assert.Equal(2, (await ListAsync(client)).Count);
        Assert.Null((await client.GetFromJsonAsync<StoredSubmissionDetail>("/api/submissions/old-1", JsonOptions))!.AiResponse);
        Assert.NotNull((await client.GetFromJsonAsync<StoredSubmissionDetail>($"/api/submissions/{newId}", JsonOptions))!.AiResponse);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static ByteArrayContent RawEml(byte[] eml)
    {
        var content = new ByteArrayContent(eml);
        content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        return content;
    }

    private static async Task<string> UploadAsync(HttpClient client, byte[] eml)
    {
        var response = await client.PostAsync("/api/submissions", RawEml(eml));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("submissionId").GetString()!;
    }

    private static async Task<ForwardRunResult> ForwardAsync(HttpClient client, int max = 5)
    {
        var response = await client.PostAsync($"/api/submissions/forward?max={max}", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ForwardRunResult>(JsonOptions))!;
    }

    private static async Task<List<StoredSubmissionSummary>> ListAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<List<StoredSubmissionSummary>>($"/api/submissions{query}", JsonOptions))!;

    private HttpClient CreateClient(StubReceiver receiver)
    {
        var factory = new WebApplicationFactory<EmlSubmissionParser>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:DatabasePath", _databasePath);
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<AiReceiverClient>().ConfigurePrimaryHttpMessageHandler(() => receiver));
        });
        return factory.CreateClient();
    }

    private static byte[] GenerateEml(int seed = 7)
    {
        var scenario = new ScenarioGenerator(TimeProvider.System).Generate(new ScenarioRequest { Seed = seed });
        var message = new SampleEmailBuilder([new SubmissionPdfGenerator()], TimeProvider.System).Build(scenario);
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private sealed class StubReceiver(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        /// <summary>When set, each request waits for it to complete, so a forwarding run can be held open.</summary>
        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Bodies)
            {
                Bodies.Add(body);
            }
            Started.TrySetResult();
            if (Hold is not null)
            {
                await Hold.Task;
            }

            using var doc = JsonDocument.Parse(body);
            var ack = new AiSubmissionAcknowledgement
            {
                SubmissionId = doc.RootElement.GetProperty("context").GetProperty("submissionId").GetString()!,
                Status = "accepted",
                ReceivedAt = DateTimeOffset.UtcNow,
                Ctp = new AiCtpModel { BrokerName = "Test Broker", AgencyCode = "DN0000001", OccupationCode = "BB06", Appetite = "A = Not Declined", Covers = ["EL", "PL"] },
            };
            return new HttpResponseMessage(status) { Content = JsonContent.Create(ack) };
        }
    }
}
