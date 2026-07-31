namespace BackendApi.Services;

// Events redesign: a Holiday-type Event now actually blocks scheduling (attendance-taking,
// exam scheduling) on that date, rather than being purely informational - real registrar
// practice ("no classes or exams will be held on designated days," per this session's
// research). Only Approved holidays block anything - a Pending or Denied holiday proposal
// has no scheduling effect until it's approved.
public interface IHolidayService
{
    Task<bool> IsHolidayAsync(Guid collegeId, DateOnly date);
}
