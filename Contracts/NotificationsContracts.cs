using System.Text.Json;

namespace BackendApi.Contracts;

// Notification Router (shared) — response shapes for GET /notifications and the
// mark-as-read endpoint. Payload is passed through as raw JSON (it's stored as nvarchar(max))
// rather than re-typed per notification_type, matching what NotificationsHub already
// pushes over SignalR for the same rows.
public record NotificationDto(Guid Id, string Type, JsonElement Payload, DateTime CreatedAt, DateTime? ReadAt, bool IsRead);

public record NotificationsPageResponse(List<NotificationDto> Notifications, int Page, int PageSize, int TotalCount);

public record MarkNotificationReadResponse(Guid Id, DateTime ReadAt);
