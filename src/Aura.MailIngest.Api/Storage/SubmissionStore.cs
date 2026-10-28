using System.Text.Json;
using Aura.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aura.MailIngest.Api.Storage;

/// <summary>
/// Development/demo store of received emails so they can be reviewed later. SQLite, created on first use with no
/// migrations. Columns added after the first release are added to existing databases by <see cref="EnsureSchema"/>;
/// for anything more drastic, delete the database file and it will be recreated.
/// </summary>
public sealed class SubmissionDbContext(DbContextOptions<SubmissionDbContext> options) : DbContext(options)
{
    public DbSet<StoredSubmission> Submissions => Set<StoredSubmission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var submission = modelBuilder.Entity<StoredSubmission>();
        submission.HasKey(s => s.SubmissionId);
        submission.HasIndex(s => s.StoredAtUtc);
        submission.Property(s => s.Status).HasConversion<string>();
        submission.Property(s => s.AIResponse).HasColumnName("AIResponse");
    }

    /// <summary>Creates the database if needed and adds columns introduced since it was created, keeping existing rows.</summary>
    public void EnsureSchema()
    {
        Database.EnsureCreated();

        var columns = Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Submissions')").ToList();
        if (!columns.Contains("AIResponse"))
        {
            Database.ExecuteSqlRaw("ALTER TABLE Submissions ADD COLUMN AIResponse TEXT NULL");
        }
    }
}

public enum SubmissionStatus
{
    Pending,
    Accepted,
    Failed,
}

public sealed class StoredSubmission
{
    public required string SubmissionId { get; init; }
    public required string Mailbox { get; init; }
    public required string From { get; init; }
    public required string Subject { get; init; }
    public required string ReceivedOn { get; init; }
    public required int AttachmentCount { get; init; }

    /// <summary>Attachment file names joined with "; ", for display without loading the raw email.</summary>
    public required string AttachmentNames { get; init; }

    /// <summary>The <c>content</c> text sent to the AI.</summary>
    public required string Content { get; init; }

    /// <summary>The original .eml exactly as received.</summary>
    public required byte[] RawEml { get; init; }

    public required DateTime StoredAtUtc { get; init; }
    public SubmissionStatus Status { get; set; } = SubmissionStatus.Pending;
    public DateTime? ForwardedAtUtc { get; set; }
    public string? Error { get; set; }

    /// <summary>The AI receiver's acknowledgement (including the AI CTP model) as JSON. Null until forwarded successfully.</summary>
    public string? AIResponse { get; set; }
}

public sealed record StoredSubmissionSummary(
    string SubmissionId,
    string Mailbox,
    string From,
    string Subject,
    string ReceivedOn,
    int AttachmentCount,
    string AttachmentNames,
    SubmissionStatus Status,
    DateTime StoredAtUtc,
    DateTime? ForwardedAtUtc,
    string? Error);

public sealed record StoredSubmissionDetail(
    StoredSubmissionSummary Summary,
    string Content,
    long EmlSizeBytes,
    AiSubmissionAcknowledgement? AiResponse);

public sealed class SubmissionStore(SubmissionDbContext db, TimeProvider timeProvider)
{
    public async Task<StoredSubmission> AddAsync(ParsedSubmission parsed, byte[] rawEml, CancellationToken ct)
    {
        var payload = parsed.Payload;
        var submission = new StoredSubmission
        {
            SubmissionId = payload.Context.SubmissionId,
            Mailbox = payload.Context.Mailbox,
            From = parsed.From,
            Subject = parsed.Subject,
            ReceivedOn = payload.Context.ReceivedOn,
            AttachmentCount = payload.Context.AttachmentCount,
            AttachmentNames = string.Join("; ", payload.AttachedDocuments.Select(d => d.FileName)),
            Content = payload.Content,
            RawEml = rawEml,
            StoredAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        };

        db.Submissions.Add(submission);
        await db.SaveChangesAsync(ct);
        return submission;
    }

    public async Task MarkForwardedAsync(StoredSubmission submission, AiSubmissionAcknowledgement ack, CancellationToken ct)
    {
        submission.Status = SubmissionStatus.Accepted;
        submission.ForwardedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        submission.Error = null;
        submission.AIResponse = JsonSerializer.Serialize(ack);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Oldest pending submissions first, tracked so their status can be updated as they are forwarded.</summary>
    public async Task<List<StoredSubmission>> GetPendingAsync(int take, CancellationToken ct) =>
        await db.Submissions
            .Where(s => s.Status == SubmissionStatus.Pending)
            .OrderBy(s => s.StoredAtUtc)
            .Take(take)
            .ToListAsync(ct);

    public Task<int> CountPendingAsync(CancellationToken ct) =>
        db.Submissions.CountAsync(s => s.Status == SubmissionStatus.Pending, ct);

    /// <summary>A temporary failure: the submission stays Pending (to be retried) with the error recorded.</summary>
    public async Task RecordRetryableErrorAsync(StoredSubmission submission, string error, CancellationToken ct)
    {
        submission.Error = error;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(StoredSubmission submission, string error, CancellationToken ct)
    {
        submission.Status = SubmissionStatus.Failed;
        submission.Error = error;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Most recent submissions as inbox rows (without the raw .eml, which is never loaded here).</summary>
    public async Task<IReadOnlyList<InboxMessage>> ListInboxAsync(int take, CancellationToken ct)
    {
        var rows = await db.Submissions.AsNoTracking()
            .OrderByDescending(s => s.StoredAtUtc)
            .Take(take)
            .Select(s => new StoredSubmission
            {
                SubmissionId = s.SubmissionId, Mailbox = s.Mailbox, From = s.From, Subject = s.Subject,
                ReceivedOn = s.ReceivedOn, AttachmentCount = s.AttachmentCount, AttachmentNames = s.AttachmentNames,
                Content = s.Content, RawEml = Array.Empty<byte>(), StoredAtUtc = s.StoredAtUtc,
                Status = s.Status, ForwardedAtUtc = s.ForwardedAtUtc, Error = s.Error, AIResponse = s.AIResponse,
            })
            .ToListAsync(ct);

        return rows.Select(Inbox.InboxMapper.ToInboxMessage).ToList();
    }

    public async Task<IReadOnlyList<StoredSubmissionSummary>> ListAsync(SubmissionStatus? status, int take, CancellationToken ct)
    {
        var query = db.Submissions.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(s => s.Status == status);
        }

        return await query
            .OrderByDescending(s => s.StoredAtUtc)
            .Take(take)
            .Select(s => new StoredSubmissionSummary(
                s.SubmissionId, s.Mailbox, s.From, s.Subject, s.ReceivedOn, s.AttachmentCount, s.AttachmentNames,
                s.Status, s.StoredAtUtc, s.ForwardedAtUtc, s.Error))
            .ToListAsync(ct);
    }

    public async Task<StoredSubmissionDetail?> GetAsync(string submissionId, CancellationToken ct)
    {
        var row = await db.Submissions.AsNoTracking()
            .Where(s => s.SubmissionId == submissionId)
            .Select(s => new
            {
                Summary = new StoredSubmissionSummary(
                    s.SubmissionId, s.Mailbox, s.From, s.Subject, s.ReceivedOn, s.AttachmentCount, s.AttachmentNames,
                    s.Status, s.StoredAtUtc, s.ForwardedAtUtc, s.Error),
                s.Content,
                EmlSizeBytes = (long)s.RawEml.Length,
                s.AIResponse,
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? null
            : new StoredSubmissionDetail(
                row.Summary,
                row.Content,
                row.EmlSizeBytes,
                row.AIResponse is null ? null : JsonSerializer.Deserialize<AiSubmissionAcknowledgement>(row.AIResponse));
    }

    public Task<byte[]?> GetRawEmlAsync(string submissionId, CancellationToken ct) =>
        db.Submissions.AsNoTracking()
            .Where(s => s.SubmissionId == submissionId)
            .Select(s => s.RawEml)
            .FirstOrDefaultAsync(ct);
}
