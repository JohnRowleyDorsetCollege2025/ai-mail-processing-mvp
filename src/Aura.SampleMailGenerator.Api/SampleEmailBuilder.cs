using System.Globalization;
using Aura.SampleMailGenerator.Api.Attachments;
using Aura.SampleMailGenerator.Api.Scenarios;
using MimeKit;

namespace Aura.SampleMailGenerator.Api;

/// <summary>Turns a scenario into a broker email (.eml) with the selected attachments.</summary>
public sealed class SampleEmailBuilder
{
    private static readonly CultureInfo IrishCulture = CultureInfo.GetCultureInfo("en-IE");

    private readonly IReadOnlyDictionary<string, IAttachmentGenerator> _generators;
    private readonly TimeProvider _timeProvider;

    public SampleEmailBuilder(IEnumerable<IAttachmentGenerator> generators, TimeProvider timeProvider)
    {
        _generators = generators.ToDictionary(g => g.Key, StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider;
    }

    public IReadOnlyCollection<string> AttachmentKeys => _generators.Keys.ToList();

    public MimeMessage Build(SubmissionScenario scenario, IReadOnlyList<string>? attachmentKeys = null)
    {
        var keys = attachmentKeys is { Count: > 0 } ? attachmentKeys : ["submission-pdf"];
        var unknown = keys.Where(k => !_generators.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown attachment type(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", AttachmentKeys)}");
        }

        var attachments = keys.Select(k => _generators[k].Generate(scenario)).ToList();

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(scenario.Broker.ContactName, scenario.Broker.Email));
        message.To.Add(new MailboxAddress(scenario.RecipientName, scenario.RecipientAddress));
        message.Subject = BuildSubject(scenario);
        message.Date = _timeProvider.GetUtcNow();
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("aura-samples.example");
        message.Headers.Add("X-Aura-Sample-Seed", scenario.Seed.ToString(CultureInfo.InvariantCulture));

        var body = new BodyBuilder { TextBody = BuildBody(scenario, attachments) };
        foreach (var attachment in attachments)
        {
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.MimeType));
        }
        message.Body = body.ToMessageBody();

        return message;
    }

    private static string BuildSubject(SubmissionScenario s) =>
        $"Submission - {s.Insured.DisplayName} - {s.RequestType}";

    private static string BuildBody(SubmissionScenario s, IReadOnlyList<GeneratedAttachment> attachments)
    {
        var rng = new Random(s.Seed);
        var greeting = Pick(rng, ["Hi team,", "Hi all,", "Good morning,", "Hello,"]);
        var deadline = s.QuoteRequiredBy.ToString("dddd d MMMM", IrishCulture);
        var inception = s.InceptionDate.ToString("d MMMM yyyy", IrishCulture);
        var attachmentList = string.Join("\n", attachments.Select(a => $"  - {a.FileName}"));

        var intro = s.RequestType == "Renewal"
            ? $"Please see attached a commercial combined renewal submission for {s.Insured.DisplayName}, renewing on {inception}. " +
              $"The risk is currently with {s.CurrentInsurer} at an expiring premium of {s.ExpiringPremium?.ToString("C0", IrishCulture)}."
            : $"Please see attached a new business commercial combined submission for {s.Insured.DisplayName}, with cover to incept on {inception}.";

        var summary = Pick(rng,
        [
            $"The client is a {s.Insured.Trade.ToLowerInvariant()} business based in {TownOf(s)}, trading for {s.Insured.YearsTrading} years " +
            $"with {s.Insured.Employees} employees and an annual turnover of {s.Insured.Turnover.ToString("C0", IrishCulture)}.",
            $"Brief overview: {s.Insured.BusinessDescription} Turnover is {s.Insured.Turnover.ToString("C0", IrishCulture)} " +
            $"with {s.Insured.Employees} staff.",
        ]);

        var claims = s.Claims.Count == 0
            ? "Claims experience is clean for the last 5 years."
            : $"There {(s.Claims.Count == 1 ? "has been 1 claim" : $"have been {s.Claims.Count} claims")} in the last 5 years; details are in the attached document.";

        var ask = Pick(rng,
        [
            $"Could you please provide your best terms by {deadline}?",
            $"We would appreciate your quotation by {deadline} at the latest.",
            $"Please let me know if you need anything further. Terms by {deadline} would be great.",
        ]);

        return $"""
            {greeting}

            {intro}

            {summary}

            {claims}

            Attached:
            {attachmentList}

            {ask}

            Kind regards,
            {s.Broker.ContactName}
            {s.Broker.FirmName}
            {s.Broker.Phone}
            """;
    }

    private static string TownOf(SubmissionScenario s) => s.Insured.Address.Split(", ")[^2];

    private static string Pick(Random rng, string[] options) => options[rng.Next(options.Length)];
}
