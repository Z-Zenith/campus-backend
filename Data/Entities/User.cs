using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("users")]
[Index("CollegeId", "Identifier", Name = "users_college_id_identifier_key", IsUnique = true)]
public partial class User
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("college_id")]
    public Guid CollegeId { get; set; }

    [Column("identifier")]
    public string Identifier { get; set; } = null!;

    [Column("password_hash")]
    public string PasswordHash { get; set; } = null!;

    [Column("totp_secret")]
    public string? TotpSecret { get; set; }

    [Column("full_name")]
    public string FullName { get; set; } = null!;

    [Column("department_id")]
    public Guid? DepartmentId { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("date_of_birth")]
    public DateOnly? DateOfBirth { get; set; }

    [InverseProperty("Teacher")]
    public virtual ICollection<Assignment> Assignments { get; set; } = new List<Assignment>();

    [InverseProperty("MarkedByNavigation")]
    public virtual ICollection<AttendanceRecord> AttendanceRecordMarkedByNavigations { get; set; } = new List<AttendanceRecord>();

    [InverseProperty("Student")]
    public virtual ICollection<AttendanceRecord> AttendanceRecordStudents { get; set; } = new List<AttendanceRecord>();

    [InverseProperty("Student")]
    public virtual ICollection<BrowsingHistory> BrowsingHistories { get; set; } = new List<BrowsingHistory>();

    [InverseProperty("Student")]
    public virtual ICollection<BrowsingHistorySummary> BrowsingHistorySummaries { get; set; } = new List<BrowsingHistorySummary>();

    [InverseProperty("ActualTeacher")]
    public virtual ICollection<ClassSession> ClassSessions { get; set; } = new List<ClassSession>();

    [InverseProperty("Owner")]
    public virtual ICollection<CodeProject> CodeProjects { get; set; } = new List<CodeProject>();

    [ForeignKey("CollegeId")]
    [InverseProperty("Users")]
    public virtual College College { get; set; } = null!;

    [InverseProperty("Student")]
    public virtual ICollection<CustomCalendarEntry> CustomCalendarEntries { get; set; } = new List<CustomCalendarEntry>();

    [ForeignKey("DepartmentId")]
    [InverseProperty("Users")]
    public virtual Department? Department { get; set; }

    [InverseProperty("Owner")]
    public virtual ICollection<Document> Documents { get; set; } = new List<Document>();

    [InverseProperty("Student")]
    public virtual ICollection<EventRegistration> EventRegistrations { get; set; } = new List<EventRegistration>();

    [InverseProperty("CreatedByNavigation")]
    public virtual ICollection<Event> Events { get; set; } = new List<Event>();

    [InverseProperty("ApprovedByNavigation")]
    public virtual ICollection<Event> EventsApproved { get; set; } = new List<Event>();

    [InverseProperty("CreatedByNavigation")]
    public virtual ICollection<ExamSchedule> ExamSchedules { get; set; } = new List<ExamSchedule>();

    [InverseProperty("ApprovedByNavigation")]
    public virtual ICollection<ExternalMark> ExternalMarkApprovedByNavigations { get; set; } = new List<ExternalMark>();

    [InverseProperty("Student")]
    public virtual ICollection<ExternalMark> ExternalMarkStudents { get; set; } = new List<ExternalMark>();

    [InverseProperty("SubmittedByNavigation")]
    public virtual ICollection<ExternalMark> ExternalMarkSubmittedByNavigations { get; set; } = new List<ExternalMark>();

    [InverseProperty("Student")]
    public virtual ICollection<FeeRecord> FeeRecords { get; set; } = new List<FeeRecord>();

    [InverseProperty("User")]
    public virtual ICollection<ClubMember> ClubMemberships { get; set; } = new List<ClubMember>();

    [InverseProperty("Author")]
    public virtual ICollection<ClubPost> ClubPosts { get; set; } = new List<ClubPost>();

    [InverseProperty("CreatedByNavigation")]
    public virtual ICollection<Club> ClubsCreated { get; set; } = new List<Club>();

    [InverseProperty("FacultyLead")]
    public virtual ICollection<Club> ClubsAsFacultyLead { get; set; } = new List<Club>();

    [InverseProperty("StudentIncharge")]
    public virtual ICollection<Club> ClubsAsStudentIncharge { get; set; } = new List<Club>();

    [InverseProperty("Author")]
    public virtual ICollection<ClassroomDiscussionPost> ClassroomDiscussionPosts { get; set; } = new List<ClassroomDiscussionPost>();

    [InverseProperty("User")]
    public virtual ICollection<StaffGroupMember> StaffGroupMemberships { get; set; } = new List<StaffGroupMember>();

    [InverseProperty("Author")]
    public virtual ICollection<StaffGroupPost> StaffGroupPosts { get; set; } = new List<StaffGroupPost>();

    [InverseProperty("CreatedByNavigation")]
    public virtual ICollection<StaffGroup> StaffGroupsCreated { get; set; } = new List<StaffGroup>();

    [InverseProperty("PublishedByNavigation")]
    public virtual ICollection<InternalMark> InternalMarkPublishedByNavigations { get; set; } = new List<InternalMark>();

    [InverseProperty("Student")]
    public virtual ICollection<InternalMark> InternalMarkStudents { get; set; } = new List<InternalMark>();

    [InverseProperty("UploadedByNavigation")]
    public virtual ICollection<Material> Materials { get; set; } = new List<Material>();

    [InverseProperty("Student")]
    public virtual ICollection<MessageThread> MessageThreadStudents { get; set; } = new List<MessageThread>();

    [InverseProperty("Teacher")]
    public virtual ICollection<MessageThread> MessageThreadTeachers { get; set; } = new List<MessageThread>();

    [InverseProperty("Sender")]
    public virtual ICollection<Message> Messages { get; set; } = new List<Message>();

    [InverseProperty("Owner")]
    public virtual ICollection<Note> Notes { get; set; } = new List<Note>();

    [InverseProperty("Recipient")]
    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    [InverseProperty("ParentUser")]
    public virtual ICollection<ParentWard> ParentWardParentUsers { get; set; } = new List<ParentWard>();

    [InverseProperty("Student")]
    public virtual ICollection<ParentWard> ParentWardStudents { get; set; } = new List<ParentWard>();

    [InverseProperty("GrantedByNavigation")]
    public virtual ICollection<PermissionGrant> PermissionGrantGrantedByNavigations { get; set; } = new List<PermissionGrant>();

    [InverseProperty("User")]
    public virtual ICollection<PermissionGrant> PermissionGrantUsers { get; set; } = new List<PermissionGrant>();

    [InverseProperty("User")]
    public virtual ICollection<RoleBinding> RoleBindings { get; set; } = new List<RoleBinding>();

    [InverseProperty("Student")]
    public virtual ICollection<SectionEnrollment> SectionEnrollments { get; set; } = new List<SectionEnrollment>();

    [InverseProperty("Teacher")]
    public virtual ICollection<SectionFeedback> SectionFeedbacks { get; set; } = new List<SectionFeedback>();

    [InverseProperty("Teacher")]
    public virtual ICollection<Subject> Subjects { get; set; } = new List<Subject>();

    [InverseProperty("Teacher")]
    public virtual ICollection<SubjectTeacher> SubjectTeachers { get; set; } = new List<SubjectTeacher>();

    [InverseProperty("Student")]
    public virtual ICollection<Submission> Submissions { get; set; } = new List<Submission>();

    [InverseProperty("Student")]
    public virtual ICollection<SuspiciousFlag> SuspiciousFlags { get; set; } = new List<SuspiciousFlag>();

    [InverseProperty("Student")]
    public virtual ICollection<TeacherFeedback> TeacherFeedbackStudents { get; set; } = new List<TeacherFeedback>();

    [InverseProperty("Teacher")]
    public virtual ICollection<TeacherFeedback> TeacherFeedbackTeachers { get; set; } = new List<TeacherFeedback>();

    [InverseProperty("Student")]
    public virtual ICollection<TeacherReport> TeacherReportStudents { get; set; } = new List<TeacherReport>();

    [InverseProperty("Teacher")]
    public virtual ICollection<TeacherReport> TeacherReportTeachers { get; set; } = new List<TeacherReport>();

    [InverseProperty("Teacher")]
    public virtual ICollection<TeacherSectionAssignment> TeacherSectionAssignments { get; set; } = new List<TeacherSectionAssignment>();

    [InverseProperty("ReviewedByNavigation")]
    public virtual ICollection<TimetableChangeRequest> TimetableChangeRequestReviewedByNavigations { get; set; } = new List<TimetableChangeRequest>();

    [InverseProperty("Teacher")]
    public virtual ICollection<TimetableChangeRequest> TimetableChangeRequestTeachers { get; set; } = new List<TimetableChangeRequest>();

    [InverseProperty("Teacher")]
    public virtual ICollection<TimetableSlot> TimetableSlots { get; set; } = new List<TimetableSlot>();

    [InverseProperty("Student")]
    public virtual ICollection<Todo> Todos { get; set; } = new List<Todo>();

    [InverseProperty("Student")]
    public virtual ICollection<UsageTelemetry> UsageTelemetries { get; set; } = new List<UsageTelemetry>();

    // #92 — DB only enforces uniqueness on user_id where is_active = true (a partial unique
    // index, see uniq_user_active_session in db/init/01_schema.sql); a new login flips the
    // previous row to is_active=false instead of deleting it, so multiple historical rows
    // accumulate per user. Modeled as a collection (not a one-to-one nav) so EF can't return
    // an arbitrary row — callers must explicitly filter .Where(s => s.IsActive) to get the
    // current session. Also load-bearing for #130/#132's session-revocation work, which
    // relies on iterating every active session for a user.
    [InverseProperty("User")]
    public virtual ICollection<UserSession> UserSessions { get; set; } = new List<UserSession>();

    [InverseProperty("RequestedByNavigation")]
    public virtual ICollection<WhitelistRequest> WhitelistRequestRequestedByNavigations { get; set; } = new List<WhitelistRequest>();

    [InverseProperty("ReviewedByNavigation")]
    public virtual ICollection<WhitelistRequest> WhitelistRequestReviewedByNavigations { get; set; } = new List<WhitelistRequest>();
}
