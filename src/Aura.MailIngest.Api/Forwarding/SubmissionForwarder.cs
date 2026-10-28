using Aura.MailIngest.Api.Inbox;
using Aura.MailIngest.Api.Storage;
using Microsoft.Extensions.Options;

namespace Aura.MailIngest.Api.Forwarding;

public sealed class ForwardingOptions
{
    public const string SectionName = "Forwarding";

    /// <summary>Upper limit on submissions sent to the AI in one run, whatever the caller asks for.</summary>
    public int MaxBatchSize { get; set; } = 20;
}

public enum ForwardOutcome
{
    /// <summary>The AI accepted it; its response is stored in AIResponse.</summary>
    Accepted,

    /// <summary>The AI rejected it outright (4xx); it will not be retried.</summary>
    Failed,

    /// <summary>A temporary problem (AI unreachable, 5xx, timeout, throttled); it stays Pending for the next run.</summary>
    Retry,
}

public sealed record ForwardItemResult(string SubmissionId, ForwardOutcome Outcome, string? Error);

public sealed record ForwardRunResult(
    int Requested,
    int Accepted,
    int Failed,
    int Retry,
    int RemainingPending,
    bool StoppedEarly,
    IReadOnlyList<ForwardItemResult> Results);

/// <summary>Ensures only one forwarding run happens at a time, so no submission is sent to the AI twice.</summary>
public sealed class ForwardingGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

/// <summary>
/// Sends pending submissions to the AI receiver, oldest first, one at a time: each AI response is awaited and
/// stored before the next submission is sent. A temporary failure stops the run, because the AI is probably
/// unavailable and the remaining submissions would fail too; they are retried on the next run.
/// </summary>
public sealed class SubmissionForwarder(
    SubmissionStore store,
    EmlSubmissionParser parser,
    AiReceiverClient receiver,
    InboxNotifier inbox,
    IOptions<IngestOptions> ingestOptions,
    IOptions<ForwardingOptions> forwardingOptions,
    ILogger<SubmissionForwarder> logger)
{
    public async Task<ForwardRunResult> ForwardPendingAsync(int requested, CancellationToken stopToken)
    {
        var batchSize = Math.Clamp(requested, 1, forwardingOptions.Value.MaxBatchSize);
        var pending = await store.GetPendingAsync(batchSize, stopToken);
        var results = new List<ForwardItemResult>();
        var stoppedEarly = false;

        foreach (var submission in pending)
        {
            // Checked between submissions only: once one is sent we always wait for and store the AI's answer,
            // otherwise the AI could have it while we still think it is pending (and send it again).
            if (stopToken.IsCancellationRequested)
            {
                stoppedEarly = true;
                break;
            }

            var result = await ForwardOneAsync(submission);
            results.Add(result);

            if (result.Outcome == ForwardOutcome.Retry)
            {
                stoppedEarly = true;
                break;
            }
        }

        var remaining = await store.CountPendingAsync(CancellationToken.None);
        logger.LogInformation(
            "Forwarding run: {Accepted} accepted, {Failed} failed, {Retry} to retry, {Remaining} still pending",
            results.Count(r => r.Outcome == ForwardOutcome.Accepted),
            results.Count(r => r.Outcome == ForwardOutcome.Failed),
            results.Count(r => r.Outcome == ForwardOutcome.Retry),
            remaining);

        return new ForwardRunResult(
            batchSize,
            results.Count(r => r.Outcome == ForwardOutcome.Accepted),
            results.Count(r => r.Outcome == ForwardOutcome.Failed),
            results.Count(r => r.Outcome == ForwardOutcome.Retry),
            remaining,
            stoppedEarly,
            results);
    }

    private async Task<ForwardItemResult> ForwardOneAsync(StoredSubmission submission)
    {
        var id = submission.SubmissionId;
        try
        {
            // Rebuild the payload from the original .eml so the AI receives exactly what arrived, under the same id.
            using var eml = new MemoryStream(submission.RawEml);
            var parsed = await parser.ParseAsync(
                eml, submission.Mailbox, ingestOptions.Value.AgentIds, CancellationToken.None, submissionId: id);

            var ack = await receiver.SubmitAsync(parsed.Payload, CancellationToken.None);
            await store.MarkForwardedAsync(submission, ack, CancellationToken.None);
            await inbox.PublishAsync(submission);
            logger.LogInformation("Submission {SubmissionId} accepted by the AI receiver", id);
            return new ForwardItemResult(id, ForwardOutcome.Accepted, null);
        }
        catch (AiReceiverException ex) when (ex.IsPermanent)
        {
            logger.LogWarning("Submission {SubmissionId} rejected by the AI receiver: {Error}", id, ex.Message);
            await store.MarkFailedAsync(submission, ex.Message, CancellationToken.None);
            await inbox.PublishAsync(submission);
            return new ForwardItemResult(id, ForwardOutcome.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is AiReceiverException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Submission {SubmissionId} could not be forwarded, will retry: {Error}", id, ex.Message);
            await store.RecordRetryableErrorAsync(submission, ex.Message, CancellationToken.None);
            await inbox.PublishAsync(submission);
            return new ForwardItemResult(id, ForwardOutcome.Retry, ex.Message);
        }
        catch (InvalidEmlException ex)
        {
            // Should not happen (it parsed on arrival), but never let one bad row block the queue.
            await store.MarkFailedAsync(submission, ex.Message, CancellationToken.None);
            await inbox.PublishAsync(submission);
            return new ForwardItemResult(id, ForwardOutcome.Failed, ex.Message);
        }
    }
}
