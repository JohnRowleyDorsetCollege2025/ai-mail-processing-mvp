using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aura.Contracts;

/// <summary>
/// The quotation request the AI produces for our CTP (Configurable Traded Product) APIs. Mirrors
/// data/sample_ctp_payload.json field for field (including its JSON names, e.g. "cytoraID", "agentkey", "underWriter"),
/// so do not rename properties without agreeing the change with the CTP API owners.
/// Everything is nullable: the AI may not be able to determine a value, and CTP may default it.
/// </summary>
public sealed record AiCtpModel
{
    /// <summary>Submission channel, e.g. "CSR".</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>CTP product code, e.g. "BMR".</summary>
    [JsonPropertyName("product")]
    public string? Product { get; init; }

    /// <summary>User the quote is created as, e.g. "Aura".</summary>
    [JsonPropertyName("user")]
    public string? User { get; init; }

    [JsonPropertyName("subProductCode")]
    public string? SubProductCode { get; init; }

    /// <summary>The insured (proposer) name.</summary>
    [JsonPropertyName("customerName")]
    public string? CustomerName { get; init; }

    [JsonPropertyName("eircode")]
    public string? Eircode { get; init; }

    [JsonPropertyName("eircodeIdentifier")]
    public string? EircodeIdentifier { get; init; }

    /// <summary>Broker agency code, e.g. "DN0012123".</summary>
    [JsonPropertyName("agencyCode")]
    public string? AgencyCode { get; init; }

    /// <summary>Internal occupation (trade) code, e.g. "BB06".</summary>
    [JsonPropertyName("occupationCode")]
    public string? OccupationCode { get; init; }

    [JsonPropertyName("companyType")]
    public string? CompanyType { get; init; }

    /// <summary>Cover section codes requested, e.g. "EL", "PL", "PRL", "BI".</summary>
    [JsonPropertyName("covers")]
    public IReadOnlyList<string>? Covers { get; init; }

    /// <summary>Date cover is required from (yyyy-MM-dd).</summary>
    [JsonPropertyName("coverDate")]
    public DateOnly? CoverDate { get; init; }

    [JsonPropertyName("hazardCode")]
    public string? HazardCode { get; init; }

    [JsonPropertyName("businessDescription")]
    public string? BusinessDescription { get; init; }

    /// <summary>Structure not yet defined by CTP (empty in the sample), so kept as raw JSON.</summary>
    [JsonPropertyName("additionalLocations")]
    public IReadOnlyList<JsonElement>? AdditionalLocations { get; init; }

    /// <summary>Structure not yet defined by CTP (empty in the sample), so kept as raw JSON.</summary>
    [JsonPropertyName("additionalPolicyHolders")]
    public IReadOnlyList<JsonElement>? AdditionalPolicyHolders { get; init; }

    /// <summary>Our submission id (the ingest API's submissionId).</summary>
    [JsonPropertyName("submissionId")]
    public string? SubmissionId { get; init; }

    [JsonPropertyName("cytoraID")]
    public string? CytoraId { get; init; }

    /// <summary>The email text the quote was derived from.</summary>
    [JsonPropertyName("mailContent")]
    public string? MailContent { get; init; }

    [JsonPropertyName("agentkey")]
    public string? AgentKey { get; init; }

    [JsonPropertyName("inceptionDate")]
    public DateTimeOffset? InceptionDate { get; init; }

    [JsonPropertyName("submissionDate")]
    public DateTimeOffset? SubmissionDate { get; init; }

    [JsonPropertyName("brokerEmail")]
    public string? BrokerEmail { get; init; }

    [JsonPropertyName("underWriter")]
    public string? UnderWriter { get; init; }

    /// <summary>The broker contact's name (a person, e.g. "Noel McCarthy").</summary>
    [JsonPropertyName("brokerName")]
    public string? BrokerName { get; init; }

    /// <summary>Current insurer, e.g. "UN - Unknown".</summary>
    [JsonPropertyName("holdingInsurerName")]
    public string? HoldingInsurerName { get; init; }

    [JsonPropertyName("personalAccidentTrade")]
    public string? PersonalAccidentTrade { get; init; }

    /// <summary>Priority as a string, e.g. "6".</summary>
    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    /// <summary>Underwriting appetite, e.g. "A = Not Declined".</summary>
    [JsonPropertyName("appetite")]
    public string? Appetite { get; init; }

    [JsonPropertyName("teamCode")]
    public string? TeamCode { get; init; }

    [JsonPropertyName("addressLine")]
    public string? AddressLine { get; init; }

    /// <summary>A string in the sample (empty), not a boolean.</summary>
    [JsonPropertyName("isAssumedAddress")]
    public string? IsAssumedAddress { get; init; }
}
