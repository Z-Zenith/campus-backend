namespace BackendApi.Contracts;

// EventType lets a Holiday (Academic Calendar work) be modeled as an Event rather than a
// parallel concept - reuses the existing college-wide/section-restricted visibility model.
// Optional on create; defaults to Academic so existing callers (TWA-15) keep working
// unchanged without setting it.
using BackendApi.Data.Entities;

// RecurrenceRule is an optional RRULE-lite string (see Services/RecurrenceRule.cs) - storage
// and validation only, no occurrence expansion (see events.recurrence_rule's schema
// comment). Optional/additive, same pattern as EventType was when it was added - existing
// callers that don't set it are unaffected.
public record CreateEventRequest(
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    List<int>? RestrictedYears,
    List<Guid>? RestrictedDepartments,
    EventType? EventType = null,
    string? RecurrenceRule = null);

// Deliberately unchanged by the Events redesign (status/recurrence/approval fields live on
// AdminEventDto instead) - EventDto is mirrored in the shared campus-api-client package
// (consumed by campus-teacher-web/campus-admin-web), and that package already has one
// pending contract-bump (task #29, still unmerged) from when EventType was added in Phase 8.
// Existing create_event holders' events are auto-approved (see CalendarController.CreateEvent),
// so this DTO's meaning is unchanged for every caller that already uses it.
public record EventDto(Guid Id, string Title, DateTime StartTime, DateTime EndTime, bool IsRegistered, EventType EventType);

// Admin-facing event management (Phase 5, extended by the Events redesign) - distinct from
// EventDto above (which is student-facing and carries a per-caller IsRegistered flag that
// has no meaning for an admin browsing every event at their college). This DTO is
// campus-backend-local (not mirrored in campus-api-client), so new fields here don't compound
// the existing cross-repo contract drift the way changing EventDto would.
public record AdminEventDto(
    Guid Id,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    List<int>? RestrictedYears,
    List<Guid>? RestrictedDepartments,
    EventType EventType,
    EventStatus Status,
    Guid? ApprovedBy,
    DateTime? ApprovedAt,
    string? RecurrenceRule);

// EventType/RecurrenceRule optional on update (same reasoning as CreateEventRequest) -
// preserves the event's existing value when the caller doesn't set it, rather than forcing
// every editor to know about/resend a field that predates their client.
public record UpdateEventRequest(
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    List<int>? RestrictedYears,
    List<Guid>? RestrictedDepartments,
    EventType? EventType = null,
    string? RecurrenceRule = null);

// Events redesign: sign-off on a Pending event proposed by an event_organizer.
public record ApproveEventRequest(bool Approve);

public record RegisterForEventResponse(Guid EventId, Guid StudentId, DateTime RegisteredAt);

// Kind is one of: college_event | todo | custom_entry | class_session.
// A registered college event is a college_event item with Extra containing "registered=true",
// not a separate parallel list.
public record CalendarItemDto(string Kind, Guid Id, string Title, DateTime Start, DateTime End, string? Extra);

public record MyCalendarResponse(List<CalendarItemDto> Items);

// SDA-14: student-personal to-dos and custom calendar entries — student-owned, no
// permission check beyond "it's mine" (see CalendarController's write endpoints).
public record CreateTodoRequest(string Title, DateTime? DueDate);

public record TodoDto(Guid Id, string Title, DateTime? DueDate, bool Completed);

public record SetTodoCompleteRequest(bool Completed);

public record CreateCustomCalendarEntryRequest(string Title, DateOnly EntryDate);

public record CustomCalendarEntryDto(Guid Id, string Title, DateOnly EntryDate);
