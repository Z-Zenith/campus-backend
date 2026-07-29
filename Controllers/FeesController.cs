using System.Security.Claims;
using System.Text.Json;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

[ApiController]
[Route("api/v1/fees")]
[Authorize]
public class FeesController(AppDbContext db, IPermissionService permissions, IConfiguration configuration, ICollegeScopeService collegeScope) : ControllerBase
{
    // AWA-04 (Track 2). One FeeRecord per link, so the link is only ever valid for the
    // exact amount/due-date it was generated with — a link can't later be reused/edited
    // to cover a different period without creating a new record.
    [HttpPost("links")]
    public async Task<ActionResult<FeeLinkResponse>> CreateLink(CreateFeeLinkRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_fees"))
        {
            return Forbid();
        }

        if (request.Amount <= 0 || request.Amount > 10_000_000m)
        {
            return BadRequest(new { error = "invalid_amount", message = "Fee amount must be greater than zero and no more than 10,000,000." });
        }

        var student = await db.Users.FindAsync(request.StudentId);
        if (student is null || student.AccountType != AccountType.Student)
        {
            return BadRequest(new { error = "unknown_student", message = "No student exists with that id." });
        }
        // #129: manage_fees is checked globally; without this, a caller at one college could
        // create a fee link (and payment obligation) against a student at any other college.
        if (!await collegeScope.IsSameCollegeAsync(userId, student.CollegeId))
        {
            return Forbid();
        }

        // #152: "in the past" is judged against the student's college's local date, not raw
        // UTC, matching the same fix applied to TimetableController.MarkAttendance.
        var college = await db.Colleges.FindAsync(student.CollegeId);
        if (request.DueDate == default || request.DueDate < CollegeClock.LocalDate(college, DateTime.UtcNow))
        {
            return BadRequest(new { error = "invalid_due_date", message = "Due date must be a real date that isn't in the past." });
        }

        var feeRecord = new FeeRecord
        {
            Id = Guid.NewGuid(),
            StudentId = request.StudentId,
            Amount = request.Amount,
            DueDate = request.DueDate,
            Status = FeeStatus.Pending,
        };
        // The Payment Gateway integration itself is out of scope here (external system,
        // per architecture doc Section 4) — the link just needs to resolve, via the
        // fee record id, to exactly this amount/due-date pair.
        feeRecord.PaymentLink = $"https://payments.campus.local/pay/{feeRecord.Id}";

        db.FeeRecords.Add(feeRecord);
        await db.SaveChangesAsync();

        return Ok(new FeeLinkResponse(feeRecord.Id, feeRecord.PaymentLink, feeRecord.Amount, feeRecord.DueDate, feeRecord.Status.ToString()));
    }

    // Admin-facing list, college-scoped via the student join (FeeRecord itself carries no
    // CollegeId). No way to see existing fee links at all existed before this - CreateLink
    // was write-only from Admin's perspective.
    [HttpGet]
    public async Task<ActionResult<List<FeeRecordDto>>> List()
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_fees"))
        {
            return Forbid();
        }

        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        var fees = await db.FeeRecords
            .Include(f => f.Student)
            .Where(f => f.Student.CollegeId == callerCollegeId)
            .OrderByDescending(f => f.DueDate)
            .Select(f => new FeeRecordDto(
                f.Id, f.StudentId, f.Student.FullName, f.Student.Identifier, f.Amount, f.DueDate, f.Status.ToString(), f.PaidAt))
            .ToListAsync();

        return Ok(fees);
    }

    // PRT-03 — pays via the (stubbed) Payment Gateway and reflects the confirmed status
    // synchronously, so the parent never needs to check a separate confirmation email.
    [HttpPost("{id}/pay")]
    public async Task<ActionResult<PayFeeResponse>> Pay(Guid id)
    {
        // #93: existence and ownership collapse into a single NotFound (not Forbid) so an
        // unauthorized caller can't distinguish "this fee doesn't exist" from "this fee isn't
        // yours" by probing IDs — matches the standardized 404-for-both convention used by
        // WardAccessFilter and BrowsingController.ApproveWhitelistRequest for other ward/scope-
        // gated resources.
        var fee = await db.FeeRecords.FindAsync(id);
        if (fee is null || await ParentWardAccess.GetAuthorizedParentIdAsync(db, User, fee.StudentId) is null)
        {
            return NotFound();
        }

        var gatewayTxnId = $"sim_{Guid.NewGuid():N}";
        var processedAt = DateTime.UtcNow;

        // Atomic conditional update closes the check-then-act race between concurrent pay
        // requests for the same fee — only the request that actually flips the status runs
        // the state transition; a losing concurrent request sees rowsUpdated == 0.
        var rowsUpdated = await db.FeeRecords
            .Where(f => f.Id == id && f.Status != FeeStatus.Paid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Status, FeeStatus.Paid)
                .SetProperty(f => f.PaidAt, processedAt));

        if (rowsUpdated == 0)
        {
            return Conflict(new { error = "already_paid", message = "This fee has already been paid." });
        }

        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            FeeRecordId = fee.Id,
            GatewayTxnId = gatewayTxnId,
            Status = "confirmed",
            ProcessedAt = processedAt,
        });
        await db.SaveChangesAsync();

        return Ok(new PayFeeResponse(fee.Id, FeeStatus.Paid.ToString(), processedAt, gatewayTxnId));
    }

    // PRT-02
    [HttpGet("ward/{studentId}")]
    [ServiceFilter(typeof(WardAccessFilter))]
    public async Task<ActionResult<IReadOnlyList<WardFeeDto>>> Ward(Guid studentId)
    {
        var fees = await db.FeeRecords
            .Where(f => f.StudentId == studentId)
            .OrderByDescending(f => f.DueDate)
            .Select(f => new WardFeeDto(f.Id, f.Amount, f.DueDate, f.Status.ToString(), f.PaidAt))
            .ToListAsync();

        return Ok(fees);
    }

    // AWA-05. No background-job runner exists anywhere in this codebase yet (other
    // periodic-style requirements — AIS-03's copy-check, SDA-13's grace-period alert —
    // are left as unimplemented stubs rather than growing one), so this is invoked
    // on a schedule externally (e.g. a periodic job hitting this endpoint) rather than
    // introducing a first-ever IHostedService for one Should-priority feature. Gated on
    // manage_fees (same permission as CreateLink) since it's a privileged, institution-
    // wide scan, not a per-request read.
    //
    // OPERATIONAL NOTE: this endpoint does nothing on its own — something (a cron job, a
    // scheduled GitHub Action, etc.) must call it periodically for reminders to actually
    // fire. That's a deployment/ops requirement this PR doesn't provision.
    //
    // Known accepted limitations, given no institution-timezone concept and no background
    // scheduler exist anywhere else in this codebase (both would be larger, separate
    // changes, not scoped to this Should-priority feature):
    //   - The reminder window and "already reminded today" check use the server's UTC
    //     calendar day, not the college's local day — a college in a non-UTC timezone can
    //     see the window shift by up to a day relative to local time.
    //   - Idempotency is per-parent-per-day, not per-fee: if a new fee enters the window
    //     for a parent who was already reminded today, that specific fee waits until
    //     tomorrow's run rather than triggering an immediate second reminder same-day.
    [HttpPost("send-reminders")]
    public async Task<ActionResult<SendFeeRemindersResponse>> SendReminders()
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_fees"))
        {
            return Forbid();
        }

        var reminderDaysBefore = configuration.GetValue("FeeReminder:DaysBeforeDue", 3);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var reminderCutoff = today.AddDays(reminderDaysBefore);

        // Joined up front rather than one ParentWards query per fee (this can scan
        // thousands of pending fees over a school year) — one query resolves every
        // (fee, parent) pair the reminder pass needs.
        var feesByParent = (await db.FeeRecords
                .Where(f => f.Status == FeeStatus.Pending && f.DueDate >= today && f.DueDate <= reminderCutoff)
                .Join(db.ParentWards, f => f.StudentId, p => p.StudentId, (f, p) => new { Fee = f, p.ParentUserId })
                .ToListAsync())
            .GroupBy(x => x.ParentUserId, x => x.Fee)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Npgsql requires DateTimeKind.Utc for a value bound to a `timestamptz` column
        // (notifications.created_at) — DateOnly.ToDateTime() always yields Unspecified,
        // which throws at execution time against real Postgres (invisible under the
        // EF Core InMemory provider used in tests, which doesn't validate DateTimeKind).
        var todayStart = DateTime.SpecifyKind(today.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

        // Batched instead of one AnyAsync per parent — same N+1 concern as the join above.
        var alreadyRemindedParentIds = await db.Notifications
            .Where(n => feesByParent.Keys.Contains(n.RecipientId)
                && n.Type == NotificationType.FeeReminder
                && n.CreatedAt >= todayStart)
            .Select(n => n.RecipientId)
            .ToHashSetAsync();

        var remindersSent = 0;
        foreach (var (parentId, fees) in feesByParent)
        {
            if (alreadyRemindedParentIds.Contains(parentId))
            {
                continue;
            }

            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                RecipientId = parentId,
                Type = NotificationType.FeeReminder,
                Payload = JsonSerializer.Serialize(new
                {
                    fees = fees.Select(f => new { feeRecordId = f.Id, studentId = f.StudentId, amount = f.Amount, dueDate = f.DueDate }),
                }),
                CreatedAt = DateTime.UtcNow,
            });
            remindersSent++;
        }

        await db.SaveChangesAsync();

        return Ok(new SendFeeRemindersResponse(remindersSent));
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
