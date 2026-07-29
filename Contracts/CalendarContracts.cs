namespace BackendApi.Contracts;

public record CreateEventRequest(
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    List<int>? RestrictedYears,
    List<Guid>? RestrictedDepartments);

public record EventDto(Guid Id, string Title, DateTime StartTime, DateTime EndTime, bool IsRegistered);

public record RegisterForEventResponse(Guid EventId, Guid StudentId, DateTime RegisteredAt);

// Kind is one of: college_event | todo | custom_entry | class_session.
// A registered college event is a college_event item with Extra containing "registered=true",
// not a separate parallel list.
public record CalendarItemDto(string Kind, Guid Id, string Title, DateTime Start, DateTime End, string? Extra);

public record MyCalendarResponse(List<CalendarItemDto> Items);

// SDA-14: student-personal to-dos and custom calendar entries — student-owned, no
// permission check beyond "it's mine" (see CalendarController's write endpoints).
// Priority is 0-3 (None/Low/Medium/High), enforced by a DB CHECK constraint.
public record CreateTodoRequest(string Title, DateTime? DueDate, int Priority = 0);

public record TodoDto(Guid Id, string Title, DateTime? DueDate, bool Completed, int Priority, DateTime CreatedAt);

public record SetTodoCompleteRequest(bool Completed);

// Partial edit: null Title/Priority means "leave unchanged". DueDate is always-authoritative
// (matches SetTodoCompleteRequest's always-authoritative Completed) — pass the current due
// date to keep it as-is, or null to clear it.
public record UpdateTodoRequest(string? Title, DateTime? DueDate, int? Priority);

public record CreateCustomCalendarEntryRequest(string Title, DateOnly EntryDate);

public record CustomCalendarEntryDto(Guid Id, string Title, DateOnly EntryDate);
