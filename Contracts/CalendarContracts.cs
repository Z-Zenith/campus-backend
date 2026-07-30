namespace BackendApi.Contracts;

// EventType lets a Holiday (Academic Calendar work) be modeled as an Event rather than a
// parallel concept - reuses the existing college-wide/section-restricted visibility model.
// Optional on create; defaults to Academic so existing callers (TWA-15) keep working
// unchanged without setting it.
using BackendApi.Data.Entities;

public record CreateEventRequest(
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    List<int>? RestrictedYears,
    List<Guid>? RestrictedDepartments,
    EventType? EventType = null);

public record EventDto(Guid Id, string Title, DateTime StartTime, DateTime EndTime, bool IsRegistered, EventType EventType);

// Admin-facing event management (Phase 5) - distinct from EventDto above (which is
// student-facing and carries a per-caller IsRegistered flag that has no meaning for an
// admin browsing every event at their college). No feature ID was ever assigned to
// list/edit/delete - CreateEvent (TWA-15/AWA-11) was the only endpoint that existed before
// this; an admin had no way to see, change, or cancel an event after creating it.
public record AdminEventDto(
    Guid Id,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    List<int>? RestrictedYears,
    List<Guid>? RestrictedDepartments,
    EventType EventType);

// EventType optional on update (same reasoning as CreateEventRequest) - preserves the
// event's existing type when the caller doesn't set it, rather than forcing every editor
// to know about/resend a field that predates their client.
public record UpdateEventRequest(
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    List<int>? RestrictedYears,
    List<Guid>? RestrictedDepartments,
    EventType? EventType = null);

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
