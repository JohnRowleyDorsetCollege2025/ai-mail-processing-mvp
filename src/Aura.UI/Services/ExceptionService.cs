using EnterpriseSearch.Models;

namespace EnterpriseSearch.Services;

/// <summary>Mock data source matching the mockup. Replace with a real backend.</summary>
public class ExceptionService
{
    private static readonly List<ExceptionCase> Items =
    [
        new("SUB-2026-000123", "New business submission - test case 014", new(2026, 10, 13, 9, 12, 0), "TEST BROKER AGENCY 07", "CTP population", "CTP write failed", ExceptionStatus.UnderReview),
        new("SUB-2026-000121", "Renewal request - test case 011 - multiple locations", new(2026, 10, 12, 16, 40, 0), null, "Email parsing", "Attachment unreadable", ExceptionStatus.Open),
        new("SUB-2026-000118", "Endorsement - test case 009", new(2026, 10, 11, 10, 5, 0), "TEST BROKER AGENCY 02", "Broker matching", "Multiple broker matches", ExceptionStatus.Open),
        new("SUB-2026-000115", "New business submission - test case 007", new(2026, 10, 10, 14, 22, 0), "TEST BROKER AGENCY 11", "Classification", "Product not recognised", ExceptionStatus.UnderReview),
        new("SUB-2026-000112", "Quote request - test case 004", new(2026, 10, 9, 8, 50, 0), null, "Email parsing", "Empty message body", ExceptionStatus.Open),
    ];

    public List<ExceptionCase> GetAll() => [.. Items];

    public void Resolve(string submissionId) => Items.RemoveAll(i => i.SubmissionId == submissionId);
}
