namespace BackendApi.Contracts;

public record WardFeeDto(Guid Id, decimal Amount, DateOnly DueDate, string Status, DateTime? PaidAt);

// Admin-facing list (distinct from Ward above, which is parent/ward-scoped) - there was no
// way for an admin to see existing fee links at all before this, only create one.
public record FeeRecordDto(
    Guid Id,
    Guid StudentId,
    string StudentFullName,
    string StudentIdentifier,
    decimal Amount,
    DateOnly DueDate,
    string Status,
    DateTime? PaidAt);

public record PayFeeResponse(Guid FeeRecordId, string Status, DateTime ProcessedAt, string GatewayTxnId);

public record CreateFeeLinkRequest(Guid StudentId, decimal Amount, DateOnly DueDate);

public record FeeLinkResponse(Guid FeeRecordId, string PaymentLink, decimal Amount, DateOnly DueDate, string Status);

// AWA-05
public record SendFeeRemindersResponse(int RemindersSent);
