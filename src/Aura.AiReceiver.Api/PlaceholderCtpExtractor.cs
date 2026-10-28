using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aura.Contracts;

/// <summary>
/// Stand-in for the AI vendor's extraction so the end-to-end flow can be demonstrated. Reads only the email text
/// (not the attachments) with simple patterns that suit the sample generator's emails: it is deliberately naive and
/// will be replaced by the vendor's response. Fields it cannot determine are left null.
/// </summary>
public static partial class PlaceholderCtpExtractor
{
    private static readonly CultureInfo IrishCulture = CultureInfo.GetCultureInfo("en-IE");

    // Keyword -> (occupation code, business description). First match wins: hotel comes first because hotel
    // descriptions also mention their restaurant. NOTE: these are NACE codes as stand-ins; CTP expects its own
    // occupation codes (e.g. "BB06"), which the real AI will supply.
    private static readonly (string Keyword, string Code, string Description)[] Occupations =
    [
        ("hotel", "55.10", "Hotels and similar accommodation"),
        ("takeaway", "56.10", "Restaurants and mobile food service activities"),
        ("restaurant", "56.10", "Restaurants and mobile food service activities"),
        ("manufactur", "25.99", "Manufacture of other fabricated metal products"),
        ("warehous", "52.10", "Warehousing and storage"),
        ("logistics", "52.10", "Warehousing and storage"),
        ("accountancy", "69.20", "Accounting, bookkeeping and auditing activities"),
        ("retail", "47.11", "Retail sale in non-specialised stores"),
    ];

    public static AiCtpModel Extract(AiSubmissionPayload payload)
    {
        var content = payload.Content;
        var (senderName, senderAddress) = ParseFrom(content);
        var occupation = Occupations.FirstOrDefault(o => content.Contains(o.Keyword, StringComparison.OrdinalIgnoreCase));
        var inception = InceptionDate(content);
        var agencyCode = senderAddress is null ? null : AgencyCodeFor(senderAddress);

        return new AiCtpModel
        {
            // Fixed values from data/sample_ctp_payload.json.
            Channel = "CSR",
            Product = "BMR",
            User = "Aura",

            SubmissionId = payload.Context.SubmissionId,
            SubmissionDate = DateTimeOffset.TryParse(payload.Context.ReceivedOn, CultureInfo.InvariantCulture, out var received) ? received : null,
            MailContent = content,

            CustomerName = Match(CustomerPattern(), content, "name"),
            BrokerName = senderName,
            BrokerEmail = senderAddress,
            AgencyCode = agencyCode,
            AgentKey = agencyCode,

            InceptionDate = inception is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            CoverDate = inception,
            HoldingInsurerName = Match(CurrentInsurerPattern(), content, "insurer") ?? "UN - Unknown",

            OccupationCode = occupation.Code,
            BusinessDescription = occupation.Description,
            Appetite = occupation.Code is null ? null : "A = Not Declined",

            // Requested covers are in the attached proposal, which this stand-in doesn't read.
            Covers = [],
            AdditionalLocations = [],
            AdditionalPolicyHolders = [],
        };
    }

    private static (string? Name, string? Address) ParseFrom(string content)
    {
        var from = FromLine().Match(content);
        return from.Success
            ? (from.Groups["name"].Value.Trim(), from.Groups["address"].Value.Trim())
            : (null, null);
    }

    private static DateOnly? InceptionDate(string content)
    {
        var text = Match(InceptionPattern(), content, "date");
        return DateOnly.TryParseExact(text, "d MMMM yyyy", IrishCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static string? Match(Regex regex, string content, string group)
    {
        var match = regex.Match(content);
        return match.Success ? match.Groups[group].Value.Trim() : null;
    }

    // Stable fake agency code from the sender's domain, in the sample's shape ("DN" + 7 digits), so every email
    // from one firm maps to the same agency.
    private static string AgencyCodeFor(string address)
    {
        var domain = address[(address.IndexOf('@') + 1)..].ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(domain));
        return $"DN{BitConverter.ToUInt32(hash) % 10_000_000:D7}";
    }

    [GeneratedRegex(@"^From: (?<name>[^<\n]*)<(?<address>[^>\n]+)>", RegexOptions.Multiline)]
    private static partial Regex FromLine();

    [GeneratedRegex(@"submission for (?<name>.+?), (?:renewing on|with cover to incept on) ")]
    private static partial Regex CustomerPattern();

    [GeneratedRegex(@"(?:renewing on|incept on) (?<date>\d{1,2} [A-Za-z]+ \d{4})")]
    private static partial Regex InceptionPattern();

    [GeneratedRegex(@"currently with (?<insurer>.+?) at an expiring premium")]
    private static partial Regex CurrentInsurerPattern();
}
