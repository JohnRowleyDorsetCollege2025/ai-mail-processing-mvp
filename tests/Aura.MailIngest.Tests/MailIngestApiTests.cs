using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aura.Contracts;
using Aura.MailIngest.Api;
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
    public async Task Multipart_upload_is_parsed_and_forwarded_to_ai_receiver()
    {
        var receiver = new StubReceiver(HttpStatusCode.Accepted);
        using var client = CreateClient(receiver);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(GenerateEml());
        file.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        form.Add(file, "file", "submission.eml");
        form.Add(new StringContent("property"), "mailbox");

        var response = await client.PostAsync("/api/submissions", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(receiver.Body);

        // Assert on the raw JSON so the wire format matches data/sample_ai_payload.json, not just our C# model.
        using var json = JsonDocument.Parse(receiver.Body);
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
    public async Task Raw_rfc822_body_is_accepted_by_preview_without_forwarding()
    {
        var receiver = new StubReceiver(HttpStatusCode.Accepted);
        using var client = CreateClient(receiver);
        var content = new ByteArrayContent(GenerateEml());
        content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");

        var response = await client.PostAsync("/api/submissions/preview", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<AiSubmissionPayload>();
        Assert.Equal("traded", payload!.Context.Mailbox);
        Assert.Equal(1, payload.Context.AttachmentCount);
        Assert.Null(receiver.Body);
    }

    [Fact]
    public async Task Receiver_failure_returns_bad_gateway()
    {
        using var client = CreateClient(new StubReceiver(HttpStatusCode.InternalServerError));
        var content = new ByteArrayContent(GenerateEml());
        content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");

        var response = await client.PostAsync("/api/submissions", content);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Empty_body_returns_bad_request()
    {
        using var client = CreateClient(new StubReceiver(HttpStatusCode.Accepted));

        var response = await client.PostAsync("/api/submissions", new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Forwarded_submission_is_stored_and_original_eml_is_downloadable()
    {
        using var client = CreateClient(new StubReceiver(HttpStatusCode.Accepted));
        var eml = GenerateEml();

        var response = await client.PostAsync("/api/submissions", RawEml(eml));
        var submissionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("submissionId").GetString();

        var list = await client.GetFromJsonAsync<List<StoredSubmissionSummary>>("/api/submissions", JsonOptions);
        var stored = Assert.Single(list!);
        Assert.Equal(submissionId, stored.SubmissionId);
        Assert.Equal(SubmissionStatus.Accepted, stored.Status);
        Assert.NotNull(stored.ForwardedAtUtc);
        Assert.StartsWith("Submission - ", stored.Subject);
        Assert.EndsWith(".pdf", stored.AttachmentNames);

        var detail = await client.GetFromJsonAsync<StoredSubmissionDetail>($"/api/submissions/{submissionId}", JsonOptions);
        Assert.StartsWith("SUBMISSION\n", detail!.Content);
        Assert.Equal(submissionId, detail.AiResponse!.SubmissionId);
        Assert.Equal(new AiCtpModel { BrokerName = "Test Brokers", BrokerId = "BRK-1", OccupationCode = "55.10", Appetite = "Refer" }, detail.AiResponse.Ctp);

        Assert.Equal(eml, await client.GetByteArrayAsync($"/api/submissions/{submissionId}/eml"));
    }

    [Fact]
    public async Task Failed_forward_is_stored_with_error()
    {
        using var client = CreateClient(new StubReceiver(HttpStatusCode.InternalServerError));
        await client.PostAsync("/api/submissions", RawEml(GenerateEml()));

        var failed = await client.GetFromJsonAsync<List<StoredSubmissionSummary>>("/api/submissions?status=Failed", JsonOptions);
        var stored = Assert.Single(failed!);
        Assert.Equal(SubmissionStatus.Failed, stored.Status);
        Assert.Contains("500", stored.Error);
        Assert.Empty((await client.GetFromJsonAsync<List<StoredSubmissionSummary>>("/api/submissions?status=Accepted", JsonOptions))!);
    }

    [Fact]
    public async Task Preview_is_not_stored_and_unknown_id_returns_not_found()
    {
        using var client = CreateClient(new StubReceiver(HttpStatusCode.Accepted));
        await client.PostAsync("/api/submissions/preview", RawEml(GenerateEml()));

        Assert.Empty((await client.GetFromJsonAsync<List<StoredSubmissionSummary>>("/api/submissions", JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/submissions/does-not-exist")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/submissions/does-not-exist/eml")).StatusCode);
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

    [Fact]
    public async Task Failed_forward_has_no_ai_response()
    {
        using var client = CreateClient(new StubReceiver(HttpStatusCode.InternalServerError));
        await client.PostAsync("/api/submissions", RawEml(GenerateEml()));

        var stored = Assert.Single((await client.GetFromJsonAsync<List<StoredSubmissionSummary>>("/api/submissions", JsonOptions))!);
        var detail = await client.GetFromJsonAsync<StoredSubmissionDetail>($"/api/submissions/{stored.SubmissionId}", JsonOptions);
        Assert.Null(detail!.AiResponse);
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
        await client.PostAsync("/api/submissions", RawEml(GenerateEml()));

        var all = (await client.GetFromJsonAsync<List<StoredSubmissionSummary>>("/api/submissions", JsonOptions))!;
        Assert.Equal(2, all.Count);
        Assert.Null((await client.GetFromJsonAsync<StoredSubmissionDetail>("/api/submissions/old-1", JsonOptions))!.AiResponse);
        var newest = all.Single(s => s.SubmissionId != "old-1");
        Assert.NotNull((await client.GetFromJsonAsync<StoredSubmissionDetail>($"/api/submissions/{newest.SubmissionId}", JsonOptions))!.AiResponse);
    }

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

    private static byte[] GenerateEml()
    {
        var scenario = new ScenarioGenerator(TimeProvider.System).Generate(new ScenarioRequest { Seed = 7 });
        var message = new SampleEmailBuilder([new SubmissionPdfGenerator()], TimeProvider.System).Build(scenario);
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private sealed class StubReceiver(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(Body);
            var ack = new AiSubmissionAcknowledgement
            {
                SubmissionId = doc.RootElement.GetProperty("context").GetProperty("submissionId").GetString()!,
                Status = "accepted",
                ReceivedAt = DateTimeOffset.UtcNow,
                Ctp = new AiCtpModel { BrokerName = "Test Brokers", BrokerId = "BRK-1", OccupationCode = "55.10", Appetite = "Refer" },
            };
            return new HttpResponseMessage(status) { Content = JsonContent.Create(ack) };
        }
    }
}
