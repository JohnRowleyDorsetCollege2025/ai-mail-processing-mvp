using System.Text;
using Aura.MailIngest.Api;
using Aura.SampleMailGenerator.Api;
using Aura.SampleMailGenerator.Api.Attachments;
using Aura.SampleMailGenerator.Api.Scenarios;
using MimeKit;

namespace Aura.MailIngest.Tests;

public class EmlSubmissionParserTests
{
    private static readonly string[] AgentIds = ["agent-1"];
    private readonly EmlSubmissionParser _parser = new(TimeProvider.System);

    [Fact]
    public async Task Generated_submission_maps_to_ai_payload()
    {
        var scenario = new ScenarioGenerator(TimeProvider.System).Generate(new ScenarioRequest { Seed = 42, RequestType = "Renewal" });
        var message = new SampleEmailBuilder([new SubmissionPdfGenerator()], TimeProvider.System).Build(scenario);

        var payload = (await _parser.ParseAsync(ToStream(message), "traded", AgentIds)).Payload;

        Assert.Equal(AgentIds, payload.AgentIds);
        Assert.True(payload.AsyncProcessing);
        Assert.Equal("traded", payload.Context.Mailbox);
        Assert.Equal(1, payload.Context.AttachmentCount);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", payload.Context.ReceivedOn);

        var document = Assert.Single(payload.AttachedDocuments);
        Assert.Equal("application/pdf", document.MimeType);
        Assert.EndsWith(".pdf", document.FileName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(Convert.FromBase64String(document.Content), 0, 5));

        var expectedHeader =
            "SUBMISSION\n" +
            $"Submission-Id: {payload.Context.SubmissionId}\n" +
            "Mailbox: traded\n" +
            $"From: {scenario.Broker.ContactName} <{scenario.Broker.Email}>\n" +
            "To: Commercial Quotes\n" +
            $"Date: {payload.Context.ReceivedOn}\n" +
            $"Subject: {message.Subject}\n" +
            $"Attachments (1): {document.FileName}\n" +
            "\nEMAIL BODY\n";
        Assert.StartsWith(expectedHeader, payload.Content);
        Assert.Contains(scenario.Insured.LegalName, payload.Content);
    }

    [Fact]
    public async Task Octet_stream_attachments_get_mime_type_from_extension_and_inline_images_are_skipped()
    {
        var builder = new BodyBuilder { HtmlBody = "<html><head><style>p{}</style></head><body><p>Hi team,</p><p>Please quote.<br>Thanks</p></body></html>" };
        builder.Attachments.Add("Claims Experience.pdf", "%PDF-1.4 test"u8.ToArray(), new ContentType("application", "octet-stream"));
        var logo = builder.LinkedResources.Add("logo.png", new byte[] { 1, 2, 3 }, new ContentType("image", "png"));
        logo.ContentId = "logo";
        var message = new MimeMessage
        {
            From = { new MailboxAddress("Broker", "broker@example.com") },
            To = { new MailboxAddress("", "quotes@example.com") },
            Subject = "Test",
            Date = new DateTimeOffset(2026, 10, 13, 13, 9, 0, TimeSpan.FromHours(1)),
            Body = builder.ToMessageBody(),
        };

        var payload = (await _parser.ParseAsync(ToStream(message), "traded", AgentIds)).Payload;

        var document = Assert.Single(payload.AttachedDocuments);
        Assert.Equal("Claims Experience.pdf", document.FileName);
        Assert.Equal("application/pdf", document.MimeType);
        Assert.Equal("2026-10-13T12:09:00Z", payload.Context.ReceivedOn);
        Assert.Contains("To: quotes@example.com\n", payload.Content);
        Assert.EndsWith("EMAIL BODY\nHi team,\nPlease quote.\nThanks", payload.Content);
    }

    [Fact]
    public async Task Forwarded_email_attachment_is_passed_through_as_eml()
    {
        var inner = new MimeMessage
        {
            From = { new MailboxAddress("Client", "client@example.com") },
            To = { new MailboxAddress("Broker", "broker@example.com") },
            Subject = "Our renewal",
            Body = new TextPart("plain") { Text = "Please renew." },
        };
        var multipart = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Forwarding the client's email." },
            new MessagePart { Message = inner, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) },
        };
        var message = new MimeMessage
        {
            From = { new MailboxAddress("Broker", "broker@example.com") },
            To = { new MailboxAddress("Quotes", "quotes@example.com") },
            Subject = "Fwd: Our renewal",
            Body = multipart,
        };

        var payload = (await _parser.ParseAsync(ToStream(message), "traded", AgentIds)).Payload;

        var document = Assert.Single(payload.AttachedDocuments);
        Assert.Equal("Our renewal.eml", document.FileName);
        Assert.Equal("message/rfc822", document.MimeType);
        Assert.Contains("Please renew.", Encoding.UTF8.GetString(Convert.FromBase64String(document.Content)));
    }

    [Fact]
    public async Task Empty_input_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidEmlException>(() => _parser.ParseAsync(new MemoryStream(), "traded", AgentIds));
    }

    private static MemoryStream ToStream(MimeMessage message)
    {
        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return stream;
    }
}
