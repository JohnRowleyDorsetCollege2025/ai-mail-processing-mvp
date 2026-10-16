using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aura.Contracts;
using Aura.MailIngest.Api;
using Aura.SampleMailGenerator.Api;
using Aura.SampleMailGenerator.Api.Attachments;
using Aura.SampleMailGenerator.Api.Scenarios;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aura.MailIngest.Tests;

public class MailIngestApiTests
{
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

    private static HttpClient CreateClient(StubReceiver receiver)
    {
        var factory = new WebApplicationFactory<EmlSubmissionParser>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<AiReceiverClient>().ConfigurePrimaryHttpMessageHandler(() => receiver)));
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
            };
            return new HttpResponseMessage(status) { Content = JsonContent.Create(ack) };
        }
    }
}
