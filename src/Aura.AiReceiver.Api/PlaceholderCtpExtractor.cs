using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aura.Contracts;

/// <summary>
/// Stand-in for the AI vendor's extraction so the end-to-end flow can be demonstrated. Uses simple keyword
/// matching on the submission text: it is deliberately naive and will be replaced by the vendor's response.
/// </summary>
public static partial class PlaceholderCtpExtractor
{
    // Keyword -> (NACE code, description, appetite). First match wins: hotel comes first because hotel
    // descriptions also mention their restaurant.
    private static readonly (string Keyword, string Code, string Description, string Appetite)[] Occupations =
    [
        ("hotel", "55.10", "Hotels and similar accommodation", "Refer"),
        ("takeaway", "56.10", "Restaurants and mobile food service activities", "Refer"),
        ("restaurant", "56.10", "Restaurants and mobile food service activities", "Refer"),
        ("manufactur", "25.99", "Manufacture of other fabricated metal products", "In"),
        ("warehous", "52.10", "Warehousing and storage", "In"),
        ("logistics", "52.10", "Warehousing and storage", "In"),
        ("accountancy", "69.20", "Accounting, bookkeeping and auditing activities", "In"),
        ("retail", "47.11", "Retail sale in non-specialised stores", "In"),
    ];

    public static AiCtpModel Extract(AiSubmissionPayload payload)
    {
        var (senderName, senderAddress) = ParseFrom(payload.Content);
        var match = Occupations.FirstOrDefault(o => payload.Content.Contains(o.Keyword, StringComparison.OrdinalIgnoreCase));

        return new AiCtpModel
        {
            BrokerName = BrokerFirm(payload.Content) ?? senderName,
            BrokerId = senderAddress is null ? null : BrokerIdFor(senderAddress),
            OccupationCode = match.Code,
            OccupationDescription = match.Description,
            Appetite = match.Appetite ?? "Refer",
        };
    }

    private static (string? Name, string? Address) ParseFrom(string content)
    {
        var from = FromLine().Match(content);
        return from.Success
            ? (from.Groups["name"].Value.Trim(), from.Groups["address"].Value.Trim())
            : (null, null);
    }

    // Generated emails end with "Kind regards,\n<contact>\n<firm>\n<phone>"; take the firm line if present.
    private static string? BrokerFirm(string content)
    {
        var signature = SignatureBlock().Match(content);
        return signature.Success ? signature.Groups["firm"].Value.Trim() : null;
    }

    // Stable fake broker id from the sender's domain, so every email from one firm maps to the same broker.
    private static string BrokerIdFor(string address)
    {
        var domain = address[(address.IndexOf('@') + 1)..].ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(domain));
        return $"BRK-{Convert.ToHexString(hash)[..6]}";
    }

    [GeneratedRegex(@"^From: (?<name>[^<\n]*)<(?<address>[^>\n]+)>", RegexOptions.Multiline)]
    private static partial Regex FromLine();

    [GeneratedRegex(@"Kind regards,\n[^\n]+\n(?<firm>[^\n]+)\n")]
    private static partial Regex SignatureBlock();
}
