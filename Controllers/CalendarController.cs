using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public class CalendarController(AppDbContext db, IPermissionService permissions) : ControllerBase
{
    // TWA-15, AWA-11
    [HttpPost("events")]
    public async Task<ActionResult<EventDto>> CreateEvent(CreateEventRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_event"))
        {
            return Forbid();
        }

        var creator = await db.Users.FindAsync(userId);
        if (creator is null)
        {
            return Unauthorized();
        }

        // #159: nothing previously rejected EndTime <= StartTime — a zero-or-negative-length
        // event silently persisted and would render nonsensically on any calendar view.
        if (request.EndTime <= request.StartTime)
        {
            return BadRequest(new { error = "invalid_time_range", message = "EndTime must be after StartTime." });
        }
        if (request.RecurrenceRule is not null && !Services.RecurrenceRule.IsValid(request.RecurrenceRule))
        {
            return BadRequest(new { error = "invalid_recurrence_rule", message = "RecurrenceRule is not a valid RRULE-lite string." });
        }

        // Events redesign: create_event is held both by the pre-existing trusted tier
        // (lecturer/hod/admin - Teacher/AdminTier accounts) and by the new event_organizer
        // role (bindable to a specific student). The permission check alone can't
        // distinguish which granted it, so gate auto-approval on account type instead - a
        // Student's event needs sign-off before it's visible to anyone else; a Teacher's or
        // Admin's is approved immediately, same as every existing caller's behavior today.
        var requiresApproval = creator.AccountType == AccountType.Student;

        var newEvent = new Event
        {
            Id = Guid.NewGuid(),
            CollegeId = creator.CollegeId,
            Title = request.Title,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            CreatedBy = userId,
            RestrictedYears = request.RestrictedYears,
            RestrictedDepartments = request.RestrictedDepartments,
            EventType = request.EventType ?? EventType.Academic,
            Status = requiresApproval ? EventStatus.Pending : EventStatus.Approved,
            ApprovedBy = requiresApproval ? null : userId,
            ApprovedAt = requiresApproval ? null : DateTime.UtcNow,
            RecurrenceRule = request.RecurrenceRule,
        };
        db.Events.Add(newEvent);
        await db.SaveChangesAsync();

        return Ok(new EventDto(newEvent.Id, newEvent.Title, newEvent.StartTime, newEvent.EndTime, false, newEvent.EventType));
    }

    // Events redesign: approval queue for events proposed by the event_organizer role.
    // Restricted to the pre-existing trusted tier (not Student) so an organizer can't
    // approve their own or another organizer's pending event.
    [HttpGet("events/pending")]
    public async Task<ActionResult<List<AdminEventDto>>> ListPendingEvents()
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_event"))
        {
            return Forbid();
        }

        var caller = await db.Users.FindAsync(userId);
        if (caller is null)
        {
            return Unauthorized();
        }
        if (caller.AccountType == AccountType.Student)
        {
            return Forbid();
        }

        var events = await db.Events
            .Where(e => e.CollegeId == caller.CollegeId && e.Status == EventStatus.Pending)
            .OrderBy(e => e.StartTime)
            .ToListAsync();
        return Ok(events.Select(ToAdminDto).ToList());
    }

    [HttpPost("events/{id}/approve")]
    public async Task<ActionResult<AdminEventDto>> ApproveEvent(Guid id, ApproveEventRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_event"))
        {
            return Forbid();
        }

        var caller = await db.Users.FindAsync(userId);
        if (caller is null)
        {
            return Unauthorized();
        }
        if (caller.AccountType == AccountType.Student)
        {
            return Forbid();
        }

        var existingEvent = await db.Events.FirstOrDefaultAsync(e => e.Id == id);
        if (existingEvent is null)
        {
            return NotFound();
        }
        if (existingEvent.CollegeId != caller.CollegeId)
        {
            return Forbid();
        }
        if (existingEvent.Status != EventStatus.Pending)
        {
            return Conflict(new { error = "not_pending", message = "This event has already been approved or denied." });
        }

        existingEvent.Status = request.Approve ? EventStatus.Approved : EventStatus.Denied;
        existingEvent.ApprovedBy = userId;
        existingEvent.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(ToAdminDto(existingEvent));
    }

    // Phase 5 - admin-facing event management. Distinct route from GET /events below
    // (student-gated, Forbid()s any non-student) - an admin had no way to see events they'd
    // created after the fact, let alone change or cancel one, before this.
    [HttpGet("events/created")]
    public async Task<ActionResult<List<AdminEventDto>>> ListCreatedEvents()
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_event"))
        {
            return Forbid();
        }

        var caller = await db.Users.FindAsync(userId);
        if (caller is null)
        {
            return Unauthorized();
        }

        var events = await db.Events
            .Where(e => e.CollegeId == caller.CollegeId)
            .OrderByDescending(e => e.StartTime)
            .ToListAsync();
        return Ok(events.Select(ToAdminDto).ToList());
    }

    [HttpPut("events/{id}")]
    public async Task<ActionResult<AdminEventDto>> UpdateEvent(Guid id, UpdateEventRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_event"))
        {
            return Forbid();
        }

        var caller = await db.Users.FindAsync(userId);
        if (caller is null)
        {
            return Unauthorized();
        }

        var existingEvent = await db.Events.FirstOrDefaultAsync(e => e.Id == id);
        if (existingEvent is null)
        {
            return NotFound();
        }
        // #126/#129-class check: a create_event holder must not be able to edit an event
        // belonging to another college by guessing/enumerating its id.
        if (existingEvent.CollegeId != caller.CollegeId)
        {
            return Forbid();
        }

        if (request.EndTime <= request.StartTime)
        {
            return BadRequest(new { error = "invalid_time_range", message = "EndTime must be after StartTime." });
        }
        if (request.RecurrenceRule is not null && !Services.RecurrenceRule.IsValid(request.RecurrenceRule))
        {
            return BadRequest(new { error = "invalid_recurrence_rule", message = "RecurrenceRule is not a valid RRULE-lite string." });
        }

        existingEvent.Title = request.Title;
        existingEvent.StartTime = request.StartTime;
        existingEvent.EndTime = request.EndTime;
        existingEvent.RestrictedYears = request.RestrictedYears;
        existingEvent.RestrictedDepartments = request.RestrictedDepartments;
        if (request.EventType is { } eventType)
        {
            existingEvent.EventType = eventType;
        }
        if (request.RecurrenceRule is not null)
        {
            existingEvent.RecurrenceRule = request.RecurrenceRule;
        }
        await db.SaveChangesAsync();

        return Ok(ToAdminDto(existingEvent));
    }

    // Cascade-deletes EventRegistrations (event_registrations.event_id is ON DELETE CASCADE)
    // - unlike Subject.Delete's dependents, a registration only means something while its
    // event still exists, so no pre-delete guard is needed here.
    [HttpDelete("events/{id}")]
    public async Task<IActionResult> DeleteEvent(Guid id)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_event"))
        {
            return Forbid();
        }

        var caller = await db.Users.FindAsync(userId);
        if (caller is null)
        {
            return Unauthorized();
        }

        var existingEvent = await db.Events.FirstOrDefaultAsync(e => e.Id == id);
        if (existingEvent is null)
        {
            return NotFound();
        }
        if (existingEvent.CollegeId != caller.CollegeId)
        {
            return Forbid();
        }

        db.Events.Remove(existingEvent);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // SDA-20
    [HttpGet("events")]
    public async Task<ActionResult<List<EventDto>>> ListEvents()
    {
        var (student, section) = await CurrentStudentSectionAsync();
        if (student is null || section is null)
        {
            return Forbid();
        }

        var registeredEventIds = await db.EventRegistrations
            .Where(r => r.StudentId == student.Id)
            .Select(r => r.EventId)
            .ToListAsync();

        var events = await EligibleEventsQuery(student.CollegeId, section).ToListAsync();
        return Ok(events.Select(e => new EventDto(e.Id, e.Title, e.StartTime, e.EndTime, registeredEventIds.Contains(e.Id), e.EventType)).ToList());
    }

    // SDA-20
    [HttpPost("events/{id}/register")]
    public async Task<ActionResult<RegisterForEventResponse>> RegisterForEvent(Guid id)
    {
        var (student, section) = await CurrentStudentSectionAsync();
        if (student is null || section is null)
        {
            return Forbid();
        }

        var isEligible = await EligibleEventsQuery(student.CollegeId, section).AnyAsync(e => e.Id == id);
        if (!isEligible)
        {
            return Forbid();
        }

        var existing = await db.EventRegistrations.FirstOrDefaultAsync(r => r.EventId == id && r.StudentId == student.Id);
        if (existing is not null)
        {
            return Ok(new RegisterForEventResponse(existing.EventId, existing.StudentId, existing.RegisteredAt));
        }

        var registration = new EventRegistration { Id = Guid.NewGuid(), EventId = id, StudentId = student.Id };
        db.EventRegistrations.Add(registration);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // #94: two concurrent registration requests for the same (event, student) can both
            // pass the existence check above before either commits — the losing request hits
            // the unique constraint on (event_id, student_id) instead of a clean response.
            // Mirrors BrowsingController.ApproveWhitelistRequest's identical race: drop the
            // speculative insert and return the row the other request actually persisted.
            db.Entry(registration).State = EntityState.Detached;
            registration = await db.EventRegistrations.SingleAsync(r => r.EventId == id && r.StudentId == student.Id);
        }

        return Ok(new RegisterForEventResponse(registration.EventId, registration.StudentId, registration.RegisteredAt));
    }

    // SDA-14
    [HttpGet("calendar/mine")]
    public async Task<ActionResult<MyCalendarResponse>> MyCalendar()
    {
        var (student, section) = await CurrentStudentSectionAsync();
        if (student is null)
        {
            return Forbid();
        }

        var items = new List<CalendarItemDto>();

        var registeredEventIds = await db.EventRegistrations
            .Where(r => r.StudentId == student.Id)
            .Select(r => r.EventId)
            .ToListAsync();

        if (section is not null)
        {
            var events = await EligibleEventsQuery(student.CollegeId, section).ToListAsync();
            items.AddRange(events.Select(e => new CalendarItemDto(
                "college_event", e.Id, e.Title, e.StartTime, e.EndTime,
                registeredEventIds.Contains(e.Id) ? "registered=true" : null)));
        }

        // #159: an undated todo used to default to DateTime.MinValue (0001-01-01), which
        // rendered as a ~2000-years-overdue item and broke any upcoming/overdue grouping on
        // the client. Omit undated todos from the dated calendar entirely rather than giving
        // them a fabricated date — there's no separate "undated" bucket in CalendarItemDto
        // (adding one is a contract change), so skipping is the safe fix here.
        var todos = await db.Todos.Where(t => t.StudentId == student.Id && t.DueDate != null).ToListAsync();
        items.AddRange(todos.Select(t => new CalendarItemDto(
            "todo", t.Id, t.Title, t.DueDate!.Value, t.DueDate!.Value,
            t.Completed ? "completed=true" : null)));

        var customEntries = await db.CustomCalendarEntries.Where(c => c.StudentId == student.Id).ToListAsync();
        items.AddRange(customEntries.Select(c =>
        {
            var start = c.EntryDate.ToDateTime(TimeOnly.MinValue);
            return new CalendarItemDto("custom_entry", c.Id, c.Title, start, start, null);
        }));

        if (section is not null)
        {
            items.AddRange(await ThisWeeksClassSessionsAsync(section.Id));
        }

        return Ok(new MyCalendarResponse(items));
    }

    // SDA-14: personal to-dos — student-owned, no permission check beyond "it's mine".
    [HttpPost("todos")]
    public async Task<ActionResult<TodoDto>> CreateTodo(CreateTodoRequest request)
    {
        var student = await CurrentStudentAsync();
        if (student is null)
        {
            return Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "title_required", message = "To-do title must not be empty." });
        }

        var todo = new Todo
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            Title = request.Title.Trim(),
            DueDate = request.DueDate,
            Completed = false,
        };
        db.Todos.Add(todo);
        await db.SaveChangesAsync();

        return Ok(ToTodoDto(todo));
    }

    [HttpPatch("todos/{id}/complete")]
    public async Task<ActionResult<TodoDto>> SetTodoComplete(Guid id, SetTodoCompleteRequest request)
    {
        var student = await CurrentStudentAsync();
        if (student is null)
        {
            return Forbid();
        }

        var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id && t.StudentId == student.Id);
        if (todo is null)
        {
            return NotFound();
        }

        todo.Completed = request.Completed;
        await db.SaveChangesAsync();

        return Ok(ToTodoDto(todo));
    }

    [HttpDelete("todos/{id}")]
    public async Task<IActionResult> DeleteTodo(Guid id)
    {
        var student = await CurrentStudentAsync();
        if (student is null)
        {
            return Forbid();
        }

        var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id && t.StudentId == student.Id);
        if (todo is null)
        {
            return NotFound();
        }

        db.Todos.Remove(todo);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // SDA-14: custom calendar entries — same student-owned model as to-dos above.
    [HttpPost("calendar/custom-entries")]
    public async Task<ActionResult<CustomCalendarEntryDto>> CreateCustomEntry(CreateCustomCalendarEntryRequest request)
    {
        var student = await CurrentStudentAsync();
        if (student is null)
        {
            return Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "title_required", message = "Custom entry title must not be empty." });
        }

        var entry = new CustomCalendarEntry
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            Title = request.Title.Trim(),
            EntryDate = request.EntryDate,
        };
        db.CustomCalendarEntries.Add(entry);
        await db.SaveChangesAsync();

        return Ok(ToCustomEntryDto(entry));
    }

    [HttpDelete("calendar/custom-entries/{id}")]
    public async Task<IActionResult> DeleteCustomEntry(Guid id)
    {
        var student = await CurrentStudentAsync();
        if (student is null)
        {
            return Forbid();
        }

        var entry = await db.CustomCalendarEntries.FirstOrDefaultAsync(c => c.Id == id && c.StudentId == student.Id);
        if (entry is null)
        {
            return NotFound();
        }

        db.CustomCalendarEntries.Remove(entry);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<User?> CurrentStudentAsync()
    {
        var student = await db.Users.FindAsync(CurrentUserId());
        return student is { AccountType: AccountType.Student } ? student : null;
    }

    private static AdminEventDto ToAdminDto(Event e) => new(
        e.Id, e.Title, e.StartTime, e.EndTime, e.RestrictedYears, e.RestrictedDepartments,
        e.EventType, e.Status, e.ApprovedBy, e.ApprovedAt, e.RecurrenceRule);

    private static TodoDto ToTodoDto(Todo t) => new(t.Id, t.Title, t.DueDate, t.Completed);

    private static CustomCalendarEntryDto ToCustomEntryDto(CustomCalendarEntry c) => new(c.Id, c.Title, c.EntryDate);

    private async Task<List<CalendarItemDto>> ThisWeeksClassSessionsAsync(Guid sectionId)
    {
        var slots = await db.TimetableSlots
            .Where(s => s.SectionId == sectionId)
            .Include(s => s.Subject)
            .Include(s => s.Teacher)
            .ToListAsync();

        // TODO: DateTime.UtcNow is used for "today" here; this can shift the
        // weekly boundary for non-UTC colleges near midnight. Needs a
        // College.TimeZone column (schema change - requires sign-off) to fix
        // properly. Tracked as a follow-up, not blocking this PR.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monday = today.AddDays(-((int)today.DayOfWeek == 0 ? 6 : (int)today.DayOfWeek - 1));

        return slots.Select(s =>
        {
            var sessionDate = monday.AddDays(s.DayOfWeek - 1);
            return new CalendarItemDto(
                "class_session",
                s.Id,
                s.Subject.Name,
                sessionDate.ToDateTime(s.StartTime),
                sessionDate.ToDateTime(s.EndTime),
                $"teacher={s.Teacher.FullName};room={s.Room}");
        }).ToList();
    }

    // Events redesign: a Pending event is a proposal awaiting sign-off - it must not appear
    // on any student-facing surface (list, calendar, registration) until approved.
    private IQueryable<Event> EligibleEventsQuery(Guid collegeId, Section section) =>
        db.Events.Where(e => e.CollegeId == collegeId &&
            e.Status == EventStatus.Approved &&
            (e.RestrictedYears == null || e.RestrictedYears.Contains(section.Year)) &&
            (e.RestrictedDepartments == null || e.RestrictedDepartments.Contains(section.DepartmentId)));

    private async Task<(User? Student, Section? Section)> CurrentStudentSectionAsync()
    {
        var userId = CurrentUserId();
        var student = await db.Users.FindAsync(userId);
        if (student is null || student.AccountType != AccountType.Student)
        {
            return (null, null);
        }

        var enrollment = await db.SectionEnrollments
            .Include(e => e.Section)
            .FirstOrDefaultAsync(e => e.StudentId == userId);
        return (student, enrollment?.Section);
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
