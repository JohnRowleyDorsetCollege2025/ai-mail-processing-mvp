namespace EnterpriseSearch.Models;

public enum ExceptionStatus { Open, UnderReview }

public record ExceptionCase(
    string SubmissionId,
    string EmailSubject,
    DateTime Received,
    string? Broker,
    string ProcessingStage,
    string ExceptionReason,
    ExceptionStatus Status);
