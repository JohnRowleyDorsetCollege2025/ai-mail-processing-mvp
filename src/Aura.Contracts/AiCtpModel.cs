using System.Text.Json.Serialization;

namespace Aura.Contracts;

/// <summary>
/// The quotation-relevant facts the AI extracts from a submission, in the shape our CTP (Configurable Traded Product)
/// APIs will consume. Provisional: fields will change once the vendor's output and the CTP mapping are agreed.
/// All fields are nullable because the AI may not be able to determine every value.
/// </summary>
public sealed record AiCtpModel
{
    [JsonPropertyName("brokerName")]
    public string? BrokerName { get; init; }

    /// <summary>Our internal broker identifier, matched from the sender.</summary>
    [JsonPropertyName("brokerId")]
    public string? BrokerId { get; init; }

    /// <summary>Occupation/trade classification code of the insured (e.g. NACE code).</summary>
    [JsonPropertyName("occupationCode")]
    public string? OccupationCode { get; init; }

    [JsonPropertyName("occupationDescription")]
    public string? OccupationDescription { get; init; }

    /// <summary>Underwriting appetite for the risk, e.g. "In", "Refer" or "Out".</summary>
    [JsonPropertyName("appetite")]
    public string? Appetite { get; init; }
}
