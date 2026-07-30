using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Services;

public class HolidayService(AppDbContext db) : IHolidayService
{
    public async Task<bool> IsHolidayAsync(Guid collegeId, DateOnly date)
    {
        // Events store timestamptz start/end, not a single date - a holiday "covers" a date
        // if that date falls anywhere in [start_time, end_time]. Compared as UTC here
        // (consistent with how the rest of this schema treats timestamptz columns); a
        // holiday whose boundary falls near local midnight for a non-UTC college is a
        // narrower version of the same #152 class of issue already disclosed elsewhere in
        // this codebase, not a new one introduced here.
        var dayStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var dayEnd = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        return await db.Events.AnyAsync(e =>
            e.CollegeId == collegeId &&
            e.EventType == EventType.Holiday &&
            e.Status == EventStatus.Approved &&
            e.StartTime <= dayEnd && e.EndTime >= dayStart);
    }
}
