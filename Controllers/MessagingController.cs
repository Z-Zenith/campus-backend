using System.Security.Claims;
using System.Text.Json;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public class MessagingController(AppDbContext db) : ControllerBase
{
    // DMS-01 (Track 2) — get-or-create the single thread for a student-teacher pair.
    // Not in the original documented API map (only send/list were), but a thread has
    // to exist before a message can be sent into it, and the DB's unique index on
    // (student_id, teacher_id) is exactly DMS-01's "scoped to exactly one pair"
    // acceptance criterion — this endpoint is what upholds it.
    [HttpPost("messages/threads")]
    public async Task<ActionResult<MessageThreadResponse>> CreateThread(CreateThreadRequest request)
    {
        if (request.StudentId == request.TeacherId)
        {
            return BadRequest("StudentId and TeacherId must be different users.");
        }

        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (caller.AccountType is not AccountType.AdminTier && caller.Id != request.StudentId && caller.Id != request.TeacherId)
        {
            return Forbid();
        }

        var student = await db.Users.FindAsync(request.StudentId);
        if (student is null || student.AccountType != AccountType.Student)
        {
            return BadRequest("StudentId must reference an existing user with account type Student.");
        }

        var teacher = await db.Users.FindAsync(request.TeacherId);
        if (teacher is null || teacher.AccountType != AccountType.Teacher)
        {
            return BadRequest("TeacherId must reference an existing user with account type Teacher.");
        }

        // #11: AdminTier bypassed the participant check above unconditionally, letting
        // an Admin at one college open a DM thread between a student and teacher at a
        // different college — same cross-college IDOR class CollegeScopeService closes
        // elsewhere (#126-#129).
        if (caller.AccountType is AccountType.AdminTier
            && (caller.CollegeId != student.CollegeId || caller.CollegeId != teacher.CollegeId))
        {
            return Forbid();
        }

        var existing = await db.MessageThreads
            .FirstOrDefaultAsync(t => t.StudentId == request.StudentId && t.TeacherId == request.TeacherId);
        if (existing is not null)
        {
            return Ok(ToResponse(existing));
        }

        var thread = new MessageThread
        {
            Id = Guid.NewGuid(),
            StudentId = request.StudentId,
            TeacherId = request.TeacherId,
            CreatedAt = DateTime.UtcNow,
        };
        db.MessageThreads.Add(thread);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // #159: two concurrent CreateThread calls for the same (student, teacher) pair
            // can both pass the "no existing thread" check above before either commits — the
            // losing request hits message_threads' unique (student_id, teacher_id) index as
            // an otherwise-uncaught DbUpdateException. Same recovery pattern as
            // BrowsingController.ApproveWhitelistRequest: drop our speculative insert and
            // return the thread the other request actually persisted.
            db.Entry(thread).State = EntityState.Detached;
            thread = await db.MessageThreads
                .SingleAsync(t => t.StudentId == request.StudentId && t.TeacherId == request.TeacherId);
            return Ok(ToResponse(thread));
        }

        return CreatedAtAction(nameof(ListMessages), new { id = thread.Id }, ToResponse(thread));
    }

    // DMS-01 (Track 2) — sender identity comes from the caller's own session, never from
    // the request body, so a participant can't send a message impersonating the other party.
    [HttpPost("messages/threads/{id}/messages")]
    public async Task<ActionResult<MessageResponse>> SendMessage(Guid id, SendMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest("Content must not be empty.");
        }

        var thread = await db.MessageThreads.FindAsync(id);
        if (thread is null)
        {
            return NotFound();
        }

        var senderId = CurrentUserId();
        if (senderId != thread.StudentId && senderId != thread.TeacherId)
        {
            return Forbid();
        }

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ThreadId = id,
            SenderId = senderId,
            Content = request.Content,
            SentAt = DateTime.UtcNow,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(ListMessages), new { id }, ToResponse(message));
    }

    // DMS-01 (Track 2) — not in the original documented API map, but required to
    // actually render a thread's conversation (sending was the only documented route).
    // Only a thread participant (or Admin) can read its messages.
    //
    // #159: paginated (page/pageSize query params, matching MarksController's [FromQuery]
    // convention elsewhere in this codebase) — an unbounded ToListAsync() over an
    // indefinitely-growing thread would otherwise load every message ever sent in it.
    [HttpGet("messages/threads/{id}/messages")]
    public async Task<ActionResult<List<MessageResponse>>> ListMessages(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var thread = await db.MessageThreads.FindAsync(id);
        if (thread is null)
        {
            return NotFound();
        }

        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (caller.AccountType is not AccountType.AdminTier && caller.Id != thread.StudentId && caller.Id != thread.TeacherId)
        {
            return Forbid();
        }

        // #11: same cross-college IDOR as CreateThread above — an Admin bypassing the
        // participant check must still be limited to threads within their own college.
        if (caller.AccountType is AccountType.AdminTier && !await IsThreadInCollegeAsync(thread, caller.CollegeId))
        {
            return Forbid();
        }

        var (skip, take) = NormalizePaging(page, pageSize);
        var messages = await db.Messages
            .Where(m => m.ThreadId == id)
            .OrderBy(m => m.SentAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        return Ok(messages.Select(ToResponse).ToList());
    }

    // DMS-01 (Track 2) — the caller's own inbox, per the requirement ("deliver it to the
    // other party's inbox in their respective app"). Always the authenticated caller's own
    // threads — never an arbitrary userId — so one account can't read another's inbox.
    //
    // #159: previously .Include(t => t.Messages) pulled every message in every thread just
    // to pick out each thread's last one, and the endpoint had no pagination at all. This
    // projects only the latest message per thread (a correlated OrderByDescending().Take(1),
    // not a full load of thread.Messages) and paginates the resulting thread list.
    [HttpGet("messages/threads")]
    public async Task<ActionResult<List<ThreadSummaryResponse>>> ListThreads([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var userId = CurrentUserId();

        var projected = await db.MessageThreads
            .Where(t => t.StudentId == userId || t.TeacherId == userId)
            .Select(t => new
            {
                t.Id,
                t.StudentId,
                t.TeacherId,
                t.CreatedAt,
                LastMessage = t.Messages
                    .OrderByDescending(m => m.SentAt)
                    .Select(m => new MessageResponse(m.Id, m.ThreadId, m.SenderId, m.Content, m.SentAt, m.ReadAt))
                    .FirstOrDefault(),
            })
            .ToListAsync();

        var (skip, take) = NormalizePaging(page, pageSize);
        var summaries = projected
            .Select(t => new ThreadSummaryResponse(t.Id, t.StudentId, t.TeacherId, t.CreatedAt, t.LastMessage))
            .OrderByDescending(s => s.LastMessage?.SentAt ?? s.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToList();

        return Ok(summaries);
    }

    // #159: shared page/pageSize clamp — page is 1-based, pageSize is capped so a caller
    // can't force an unbounded query by passing an absurdly large value.
    private static (int Skip, int Take) NormalizePaging(int page, int pageSize)
    {
        var normalizedPage = Math.Max(page, 1);
        var normalizedSize = Math.Clamp(pageSize, 1, 200);
        return ((normalizedPage - 1) * normalizedSize, normalizedSize);
    }

    // Notification Router (shared) — the caller's own notifications, most recent first
    // (matches the idx_notifications_recipient index's (recipient_id, created_at DESC)
    // ordering), paginated. Always scoped to the authenticated caller — never an
    // arbitrary userId — so one account can never read another's notifications.
    [HttpGet("notifications")]
    public async Task<ActionResult<NotificationsPageResponse>> ListNotifications([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var userId = CurrentUserId();
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 100 ? 20 : pageSize;

        var query = db.Notifications.Where(n => n.RecipientId == userId);
        var totalCount = await query.CountAsync();

        var notifications = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new NotificationsPageResponse(notifications.Select(ToDto).ToList(), page, pageSize, totalCount));
    }

    // Notification Router (shared) — marks one of the caller's own notifications read.
    // Idempotent: re-marking an already-read notification just returns its existing
    // readAt rather than erroring or clobbering the original read time. Ownership check
    // collapses "doesn't exist" and "isn't yours" into the same 404, same pattern used
    // elsewhere in this codebase (e.g. FeesController.Pay) to avoid leaking existence.
    [HttpPost("notifications/{id}/read")]
    public async Task<ActionResult<MarkNotificationReadResponse>> MarkNotificationRead(Guid id)
    {
        var userId = CurrentUserId();
        var notification = await db.Notifications.FindAsync(id);
        if (notification is null || notification.RecipientId != userId)
        {
            return NotFound();
        }

        notification.ReadAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(new MarkNotificationReadResponse(notification.Id, notification.ReadAt!.Value));
    }

    // #11: CreateThread only rejects a cross-college pairing when the caller is
    // AdminTier — a student or teacher can self-initiate a thread with a counterpart at a
    // different college (no same-college check applies to them), so an existing thread's
    // student and teacher are not guaranteed to share a college. Checking only one side here
    // would let an Admin who shares a college with just the student (not the teacher) read a
    // thread involving that other college — the same IDOR class this fix is meant to close.
    private async Task<bool> IsThreadInCollegeAsync(MessageThread thread, Guid collegeId) =>
        await db.Users.AnyAsync(u => u.Id == thread.StudentId && u.CollegeId == collegeId)
        && await db.Users.AnyAsync(u => u.Id == thread.TeacherId && u.CollegeId == collegeId);

    private static MessageThreadResponse ToResponse(MessageThread thread) =>
        new(thread.Id, thread.StudentId, thread.TeacherId, thread.CreatedAt);

    private static MessageResponse ToResponse(Message message) =>
        new(message.Id, message.ThreadId, message.SenderId, message.Content, message.SentAt, message.ReadAt);

    private static NotificationDto ToDto(Notification n)
    {
        using var doc = JsonDocument.Parse(n.Payload);
        return new NotificationDto(n.Id, n.Type.ToString(), doc.RootElement.Clone(), n.CreatedAt, n.ReadAt, n.ReadAt is not null);
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private async Task<User?> CurrentUserAsync() => await db.Users.FindAsync(CurrentUserId());
}
