using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Aura.Contracts;
using MimeKit;

namespace Aura.MailIngest.Api;

/// <summary>Converts a raw RFC 822 (.eml) message into the payload expected by the AI processing API.</summary>
public sealed partial class EmlSubmissionParser
{
    private readonly TimeProvider _timeProvider;

    public EmlSubmissionParser(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task<ParsedSubmission> ParseAsync(
        Stream eml,
        string mailbox,
        IReadOnlyList<string> agentIds,
        CancellationToken cancellationToken = default,
        string? submissionId = null)
    {
        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(eml, cancellationToken);
        }
        catch (FormatException ex)
        {
            throw new InvalidEmlException("The uploaded file is not a valid RFC 822 message.", ex);
        }

        if (message.From.Count == 0 && message.Body is null)
        {
            throw new InvalidEmlException("The uploaded file does not contain any message headers or body.");
        }

        // A stored email is re-parsed when it is forwarded later; it keeps the id it was given on arrival.
        submissionId ??= Guid.NewGuid().ToString();
        var receivedOn = FormatUtc(message.Date == DateTimeOffset.MinValue ? _timeProvider.GetUtcNow() : message.Date);
        var documents = ExtractAttachments(message, cancellationToken);

        var content = BuildContent(message, submissionId, mailbox, receivedOn, documents);

        var payload = new AiSubmissionPayload
        {
            AgentIds = agentIds,
            Content = content,
            Context = new SubmissionContext
            {
                SubmissionId = submissionId,
                Mailbox = mailbox,
                ReceivedOn = receivedOn,
                AttachmentCount = documents.Count,
            },
            AttachedDocuments = documents,
            AsyncProcessing = true,
        };

        return new ParsedSubmission(payload, FormatSenders(message.From), message.Subject ?? string.Empty);
    }

    private static string BuildContent(
        MimeMessage message,
        string submissionId,
        string mailbox,
        string receivedOn,
        IReadOnlyList<AttachedDocument> documents)
    {
        var sb = new StringBuilder();
        sb.Append("SUBMISSION\n");
        sb.Append("Submission-Id: ").Append(submissionId).Append('\n');
        sb.Append("Mailbox: ").Append(mailbox).Append('\n');
        sb.Append("From: ").Append(FormatSenders(message.From)).Append('\n');
        sb.Append("To: ").Append(FormatRecipients(message.To)).Append('\n');
        if (message.Cc.Count > 0)
        {
            sb.Append("Cc: ").Append(FormatRecipients(message.Cc)).Append('\n');
        }
        sb.Append("Date: ").Append(receivedOn).Append('\n');
        sb.Append("Subject: ").Append(message.Subject ?? string.Empty).Append('\n');
        sb.Append("Attachments (").Append(documents.Count).Append("): ")
          .Append(string.Join("; ", documents.Select(d => d.FileName))).Append('\n');
        sb.Append('\n');
        sb.Append("EMAIL BODY\n");
        sb.Append(ExtractBodyText(message));
        return sb.ToString();
    }

    // Senders keep their address (as in the sample payload) because the broker's email identifies the broker.
    private static string FormatSenders(InternetAddressList addresses) =>
        string.Join("; ", addresses.Mailboxes.Select(m =>
            string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));

    // Recipients are our own mailboxes, so the display name is enough.
    private static string FormatRecipients(InternetAddressList addresses) =>
        string.Join("; ", addresses.Mailboxes.Select(m =>
            string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name));

    private static string ExtractBodyText(MimeMessage message)
    {
        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            text = HtmlToPlainText(message.HtmlBody);
        }

        return (text ?? string.Empty).ReplaceLineEndings("\n").Trim();
    }

    /// <summary>Fallback for HTML-only emails: keeps paragraph/line structure and drops markup.</summary>
    private static string HtmlToPlainText(string html)
    {
        var text = HiddenBlocks().Replace(html, string.Empty);
        text = LineBreakTags().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()));
        return ExcessBlankLines().Replace(text, "\n\n");
    }

    [GeneratedRegex(@"<(script|style|head)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|tr|li|h[1-6]|table)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();

    private static List<AttachedDocument> ExtractAttachments(MimeMessage message, CancellationToken cancellationToken)
    {
        var documents = new List<AttachedDocument>();

        foreach (var entity in message.BodyParts)
        {
            if (!IsDocument(entity))
            {
                continue;
            }

            var index = documents.Count + 1;
            using var buffer = new MemoryStream();
            string fileName;
            string mimeType;

            switch (entity)
            {
                case MessagePart { Message: not null } forwarded:
                    // An attached email (e.g. a forwarded broker message) is passed through as .eml.
                    forwarded.Message.WriteTo(buffer, cancellationToken);
                    fileName = entity.ContentDisposition?.FileName
                        ?? $"{SanitiseFileName(forwarded.Message.Subject) ?? $"attachment-{index}"}.eml";
                    mimeType = "message/rfc822";
                    break;

                case MimePart part when part.Content is not null:
                    part.Content.DecodeTo(buffer, cancellationToken);
                    fileName = part.FileName ?? $"attachment-{index}";
                    mimeType = ResolveMimeType(part.ContentType.MimeType, fileName);
                    break;

                default:
                    continue;
            }

            documents.Add(new AttachedDocument
            {
                FileName = fileName,
                MimeType = mimeType,
                Content = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length),
            });
        }

        return documents;
    }

    /// <summary>
    /// Includes explicit attachments plus named non-image parts sent inline (some mail clients mark PDFs inline).
    /// Inline images are excluded as they are usually signature logos.
    /// </summary>
    private static bool IsDocument(MimeEntity entity)
    {
        if (entity.IsAttachment)
        {
            return true;
        }

        if (entity is MimePart part && !string.IsNullOrEmpty(part.FileName))
        {
            return !part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ResolveMimeType(string declared, string fileName)
    {
        // Brokers' mail systems frequently send everything as octet-stream; infer the real type from the extension.
        if (declared.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return MimeTypes.GetMimeType(fileName);
        }

        return declared.ToLowerInvariant();
    }

    private static string? SanitiseFileName(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(subject.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>The AI payload plus email details useful for logging and storage that are not sent to the AI as fields.</summary>
public sealed record ParsedSubmission(AiSubmissionPayload Payload, string From, string Subject);

public sealed class InvalidEmlException : Exception
{
    public InvalidEmlException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
