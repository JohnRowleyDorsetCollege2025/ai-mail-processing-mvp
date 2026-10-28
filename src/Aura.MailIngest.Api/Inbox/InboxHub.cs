using System.Text.Json;
using Aura.Contracts;
using Aura.MailIngest.Api.Storage;
using Microsoft.AspNetCore.SignalR;

namespace Aura.MailIngest.Api.Inbox;

/// <summary>
/// Pushes inbox changes to connected clients (the Aura.Inbox.UI). Clients only listen: the server sends
/// <see cref="InboxHubMethods.MessageUpserted"/> whenever a submission is stored or its status changes.
/// </summary>
public sealed class InboxHub : Hub
{
    public const string Path = "/hubs/inbox";
}

public sealed class InboxNotifier(IHubContext<InboxHub> hub, ILogger<InboxNotifier> logger)
{
    /// <summary>Broadcasts the submission's current state. Failures are logged, never thrown: a UI update must not fail ingestion.</summary>
    public async Task PublishAsync(StoredSubmission submission)
    {
        try
        {
            await hub.Clients.All.SendAsync(InboxHubMethods.MessageUpserted, InboxMapper.ToInboxMessage(submission));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not notify inbox clients about submission {SubmissionId}", submission.SubmissionId);
        }
    }
}

public static class InboxMapper
{
    private const string BodyMarker = "\nEMAIL BODY\n";

    public static InboxMessage ToInboxMessage(StoredSubmission s)
    {
        var (name, email) = ParseSender(s.From);
        var bodyStart = s.Content.IndexOf(BodyMarker, StringComparison.Ordinal);

        return new InboxMessage
        {
            Id = s.SubmissionId,
            Subject = s.Subject,
            Body = bodyStart < 0 ? s.Content : s.Content[(bodyStart + BodyMarker.Length)..],
            SenderName = name,
            SenderEmail = email,
            Status = s.Status switch
            {
                SubmissionStatus.Accepted => "Processed",
                SubmissionStatus.Failed => "Failed",
                _ => "Pending",
            },
            ReceivedOn = s.ReceivedOn,
            AttachmentNames = s.AttachmentNames.Split("; ", StringSplitOptions.RemoveEmptyEntries),
            Error = s.Error,
            Ctp = s.AIResponse is null ? null : JsonSerializer.Deserialize<AiSubmissionAcknowledgement>(s.AIResponse)?.Ctp,
        };
    }

    // Stored as "Name <address>" (or just the address); with several senders, the first one is shown.
    private static (string Name, string Email) ParseSender(string from)
    {
        var first = from.Split("; ")[0].Trim();
        var open = first.LastIndexOf('<');
        if (open < 0 || !first.EndsWith('>'))
        {
            return (first, first);
        }

        var email = first[(open + 1)..^1];
        var name = first[..open].Trim();
        return (name.Length == 0 ? email : name, email);
    }
}
