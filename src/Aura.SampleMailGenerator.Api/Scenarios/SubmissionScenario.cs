namespace Aura.SampleMailGenerator.Api.Scenarios;

/// <summary>
/// The facts behind a generated broker submission. This is the "ground truth" the email and attachments are
/// rendered from, so it can later be compared against the quotation structure the AI extracts.
/// </summary>
public sealed record SubmissionScenario
{
    public required int Seed { get; init; }
    public required Broker Broker { get; init; }
    public required Insured Insured { get; init; }
    public required string RequestType { get; init; }            // "New Business" | "Renewal"
    public required DateOnly InceptionDate { get; init; }
    public required DateOnly QuoteRequiredBy { get; init; }
    public string? CurrentInsurer { get; init; }
    public decimal? ExpiringPremium { get; init; }
    public required IReadOnlyList<CoverRequested> Covers { get; init; }
    public required IReadOnlyList<ClaimRecord> Claims { get; init; }
    public required string RecipientName { get; init; }
    public required string RecipientAddress { get; init; }
}

public sealed record Broker(string FirmName, string ContactName, string Email, string Phone, string Address);

public sealed record Insured
{
    public required string LegalName { get; init; }
    public string? TradingName { get; init; }
    public required string CroNumber { get; init; }
    public required string Address { get; init; }
    public required string Trade { get; init; }
    public required string BusinessDescription { get; init; }
    public required int YearsTrading { get; init; }
    public required int Employees { get; init; }
    public required decimal Turnover { get; init; }
    public required decimal Wageroll { get; init; }

    public string DisplayName => TradingName is null ? LegalName : $"{LegalName} T/As {TradingName}";
}

/// <param name="Section">e.g. "Employers Liability", "Buildings".</param>
/// <param name="Basis">"Limit of Indemnity" for liability, "Sum Insured" for property.</param>
public sealed record CoverRequested(string Section, string Basis, decimal Amount, decimal? Excess);

public sealed record ClaimRecord(DateOnly Date, string Section, string Description, decimal Amount, string Status);
