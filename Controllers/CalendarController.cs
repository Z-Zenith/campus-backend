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
public class CalendarController(AppDbContext db, IAppAuthorizationService permissions) : ControllerBase
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
        };
        db.Events.Add(newEvent);
        await db.SaveChangesAsync();

        return Ok(new EventDto(newEvent.Id, newEvent.Title, newEvent.StartTime, newEvent.EndTime, false));
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
        return Ok(events.Select(e => new EventDto(e.Id, e.Title, e.StartTime, e.EndTime, registeredEventIds.Contains(e.Id))).ToList());
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
            var college = await db.Colleges.FindAsync(student.CollegeId);
            items.AddRange(await ThisWeeksClassSessionsAsync(section.Id, college));
        }

        return Ok(new MyCalendarResponse(items));
    }

    // SDA-14: the full to-do list (dated and undated), for the desktop app's standalone
    // Todos card. Deliberately separate from calendar/mine's todo sub-query, which omits
    // undated todos on purpose (#159) since it has no "undated" bucket to place them in —
    // this endpoint isn't calendar-grid-shaped, so it has no such constraint.
    [HttpGet("todos/mine")]
    public async Task<ActionResult<List<TodoDto>>> MyTodos()
    {
        var student = await CurrentStudentAsync();
        if (student is null)
        {
            return Forbid();
        }

        var todos = await db.Todos
            .Where(t => t.StudentId == student.Id)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync();

        return Ok(todos.Select(ToTodoDto).ToList());
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
            Priority = request.Priority,
            CreatedAt = DateTime.UtcNow,
        };
        db.Todos.Add(todo);
        await db.SaveChangesAsync();

        return Ok(ToTodoDto(todo));
    }

    [HttpPatch("todos/{id}")]
    public async Task<ActionResult<TodoDto>> UpdateTodo(Guid id, UpdateTodoRequest request)
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

        if (request.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return BadRequest(new { error = "title_required", message = "To-do title must not be empty." });
            }
            todo.Title = request.Title.Trim();
        }
        todo.DueDate = request.DueDate;
        if (request.Priority.HasValue)
        {
            todo.Priority = request.Priority.Value;
        }

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

    private static TodoDto ToTodoDto(Todo t) => new(t.Id, t.Title, t.DueDate, t.Completed, t.Priority, t.CreatedAt);

    private static CustomCalendarEntryDto ToCustomEntryDto(CustomCalendarEntry c) => new(c.Id, c.Title, c.EntryDate);

    private async Task<List<CalendarItemDto>> ThisWeeksClassSessionsAsync(Guid sectionId, College? college)
    {
        var slots = await db.TimetableSlots
            .Where(s => s.SectionId == sectionId)
            .Include(s => s.Subject)
            .Include(s => s.Teacher)
            .ToListAsync();

        // #152-class fix: derive "today" from the college's local time zone (same helper
        // TimetableController/FeesController already use) instead of raw UTC, which used to
        // shift the weekly boundary for non-UTC colleges near local midnight.
        var today = CollegeClock.LocalDate(college, DateTime.UtcNow);
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

    private IQueryable<Event> EligibleEventsQuery(Guid collegeId, Section section) =>
        db.Events.Where(e => e.CollegeId == collegeId &&
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
