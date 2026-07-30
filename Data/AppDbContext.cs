using System;
using System.Collections.Generic;
using BackendApi.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data;

public partial class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<AiDetectionReport> AiDetectionReports { get; set; }

    public virtual DbSet<Assignment> Assignments { get; set; }

    public virtual DbSet<AttendanceRecord> AttendanceRecords { get; set; }

    public virtual DbSet<AutogradeSuggestion> AutogradeSuggestions { get; set; }

    public virtual DbSet<BrowsingHistory> BrowsingHistories { get; set; }

    public virtual DbSet<BrowsingHistorySummary> BrowsingHistorySummaries { get; set; }

    public virtual DbSet<ClassSession> ClassSessions { get; set; }

    public virtual DbSet<CodeFile> CodeFiles { get; set; }

    public virtual DbSet<CodeProject> CodeProjects { get; set; }

    public virtual DbSet<College> Colleges { get; set; }

    public virtual DbSet<CopyCheckFlag> CopyCheckFlags { get; set; }

    public virtual DbSet<CustomCalendarEntry> CustomCalendarEntries { get; set; }

    public virtual DbSet<Department> Departments { get; set; }

    public virtual DbSet<Document> Documents { get; set; }

    public virtual DbSet<Event> Events { get; set; }

    public virtual DbSet<EventRegistration> EventRegistrations { get; set; }

    public virtual DbSet<ExternalMark> ExternalMarks { get; set; }

    public virtual DbSet<FeeRecord> FeeRecords { get; set; }

    public virtual DbSet<Group> Groups { get; set; }

    public virtual DbSet<GroupMember> GroupMembers { get; set; }

    public virtual DbSet<GroupPost> GroupPosts { get; set; }

    public virtual DbSet<InternalMark> InternalMarks { get; set; }

    public virtual DbSet<Material> Materials { get; set; }

    public virtual DbSet<Message> Messages { get; set; }

    public virtual DbSet<MessageThread> MessageThreads { get; set; }

    public virtual DbSet<Note> Notes { get; set; }

    public virtual DbSet<NoteLink> NoteLinks { get; set; }

    public virtual DbSet<Notification> Notifications { get; set; }

    public virtual DbSet<ParentWard> ParentWards { get; set; }

    public virtual DbSet<PaymentTransaction> PaymentTransactions { get; set; }

    public virtual DbSet<Permission> Permissions { get; set; }

    public virtual DbSet<PermissionGrant> PermissionGrants { get; set; }

    public virtual DbSet<PlagiarismReport> PlagiarismReports { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<RoleBinding> RoleBindings { get; set; }

    public virtual DbSet<Section> Sections { get; set; }

    public virtual DbSet<SectionEnrollment> SectionEnrollments { get; set; }

    public virtual DbSet<SectionFeedback> SectionFeedbacks { get; set; }

    public virtual DbSet<Subject> Subjects { get; set; }

    public virtual DbSet<Submission> Submissions { get; set; }

    public virtual DbSet<SuspiciousFlag> SuspiciousFlags { get; set; }

    public virtual DbSet<TeacherFeedback> TeacherFeedbacks { get; set; }

    public virtual DbSet<TeacherReport> TeacherReports { get; set; }

    public virtual DbSet<TeacherSectionAssignment> TeacherSectionAssignments { get; set; }

    public virtual DbSet<TimetableChangeRequest> TimetableChangeRequests { get; set; }

    public virtual DbSet<TimetableSlot> TimetableSlots { get; set; }

    public virtual DbSet<Todo> Todos { get; set; }

    public virtual DbSet<UsageTelemetry> UsageTelemetries { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserSession> UserSessions { get; set; }

    public virtual DbSet<WhitelistRequest> WhitelistRequests { get; set; }

    public virtual DbSet<WhitelistSite> WhitelistSites { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresEnum<AccountType>()
            .HasPostgresEnum<AssignmentType>()
            .HasPostgresEnum<AttendanceStatus>()
            .HasPostgresEnum<DocType>()
            .HasPostgresEnum<FeeStatus>()
            .HasPostgresEnum<GroupType>()
            .HasPostgresEnum<NotificationType>()
            .HasPostgresEnum<OcrStatus>()
            .HasPostgresEnum<PaymentTransactionStatus>()
            .HasPostgresEnum<ScopeKind>()
            .HasPostgresEnum<TimetableChangeRequestStatus>()
            .HasPostgresEnum<WhitelistRequestStatus>()
            .HasPostgresExtension("pgcrypto");

        modelBuilder.Entity<AiDetectionReport>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ai_detection_reports_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CheckedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Submission).WithMany(p => p.AiDetectionReports).HasConstraintName("ai_detection_reports_submission_id_fkey");
        });

        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("assignments_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Type).HasColumnType("assignment_type");

            entity.HasOne(d => d.Subject).WithMany(p => p.Assignments).HasConstraintName("assignments_subject_id_fkey");

            entity.HasOne(d => d.Teacher).WithMany(p => p.Assignments)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("assignments_teacher_id_fkey");
        });

        modelBuilder.Entity<AttendanceRecord>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("attendance_records_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.MarkedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Status).HasColumnType("attendance_status");

            entity.HasOne(d => d.ClassSession).WithMany(p => p.AttendanceRecords).HasConstraintName("attendance_records_class_session_id_fkey");

            entity.HasOne(d => d.MarkedByNavigation).WithMany(p => p.AttendanceRecordMarkedByNavigations)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("attendance_records_marked_by_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.AttendanceRecordStudents).HasConstraintName("attendance_records_student_id_fkey");
        });

        modelBuilder.Entity<AutogradeSuggestion>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("autograde_suggestions_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.Submission).WithMany(p => p.AutogradeSuggestions).HasConstraintName("autograde_suggestions_submission_id_fkey");
        });

        modelBuilder.Entity<BrowsingHistory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("browsing_history_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.VisitedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Student).WithMany(p => p.BrowsingHistories).HasConstraintName("browsing_history_student_id_fkey");
        });

        modelBuilder.Entity<BrowsingHistorySummary>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("browsing_history_summaries_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.GeneratedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Student).WithMany(p => p.BrowsingHistorySummaries).HasConstraintName("browsing_history_summaries_student_id_fkey");
        });

        modelBuilder.Entity<ClassSession>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("class_sessions_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.ActualTeacher).WithMany(p => p.ClassSessions)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("class_sessions_actual_teacher_id_fkey");

            entity.HasOne(d => d.TimetableSlot).WithMany(p => p.ClassSessions).HasConstraintName("class_sessions_timetable_slot_id_fkey");
        });

        modelBuilder.Entity<CodeProject>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("code_projects_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Stdin).HasDefaultValueSql("''::text");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Owner).WithMany(p => p.CodeProjects).HasConstraintName("code_projects_owner_id_fkey");
        });

        modelBuilder.Entity<CodeFile>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("code_files_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Content).HasDefaultValueSql("''::text");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Project).WithMany(p => p.CodeFiles).HasConstraintName("code_files_project_id_fkey");
        });

        modelBuilder.Entity<College>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("colleges_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.TimeZone).HasDefaultValue("UTC");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<CopyCheckFlag>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("copy_check_flags_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.FlaggedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.SubmissionA).WithMany(p => p.CopyCheckFlagSubmissionAs).HasConstraintName("copy_check_flags_submission_a_id_fkey");

            entity.HasOne(d => d.SubmissionB).WithMany(p => p.CopyCheckFlagSubmissionBs).HasConstraintName("copy_check_flags_submission_b_id_fkey");
        });

        modelBuilder.Entity<CustomCalendarEntry>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("custom_calendar_entries_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.Student).WithMany(p => p.CustomCalendarEntries).HasConstraintName("custom_calendar_entries_student_id_fkey");
        });

        modelBuilder.Entity<Department>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("departments_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.College).WithMany(p => p.Departments).HasConstraintName("departments_college_id_fkey");

            entity.HasOne(d => d.HodRoleBinding).WithMany(p => p.Departments)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("departments_hod_fk");
        });

        modelBuilder.Entity<Document>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("documents_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.DocType).HasColumnType("doc_type");
            entity.Property(e => e.OcrStatus).HasColumnType("ocr_status");

            entity.HasOne(d => d.Owner).WithMany(p => p.Documents).HasConstraintName("documents_owner_id_fkey");
        });

        modelBuilder.Entity<Event>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("events_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.College).WithMany(p => p.Events).HasConstraintName("events_college_id_fkey");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.Events)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("events_created_by_fkey");
        });

        modelBuilder.Entity<EventRegistration>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("event_registrations_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.RegisteredAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Event).WithMany(p => p.EventRegistrations).HasConstraintName("event_registrations_event_id_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.EventRegistrations).HasConstraintName("event_registrations_student_id_fkey");
        });

        modelBuilder.Entity<ExternalMark>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("external_marks_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.SubmittedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.ApprovedByNavigation).WithMany(p => p.ExternalMarkApprovedByNavigations)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("external_marks_approved_by_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.ExternalMarkStudents).HasConstraintName("external_marks_student_id_fkey");

            entity.HasOne(d => d.Subject).WithMany(p => p.ExternalMarks).HasConstraintName("external_marks_subject_id_fkey");

            entity.HasOne(d => d.SubmittedByNavigation).WithMany(p => p.ExternalMarkSubmittedByNavigations)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("external_marks_submitted_by_fkey");
        });

        modelBuilder.Entity<FeeRecord>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("fee_records_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Status).HasColumnType("fee_status");

            entity.HasOne(d => d.Student).WithMany(p => p.FeeRecords).HasConstraintName("fee_records_student_id_fkey");
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("groups_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Type).HasColumnType("group_type");

            entity.HasOne(d => d.College).WithMany(p => p.Groups).HasConstraintName("groups_college_id_fkey");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.Groups)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("groups_created_by_fkey");

            entity.HasOne(d => d.Section).WithMany(p => p.Groups)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("groups_section_id_fkey");
        });

        modelBuilder.Entity<GroupMember>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("group_members_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.JoinedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Group).WithMany(p => p.GroupMembers).HasConstraintName("group_members_group_id_fkey");

            entity.HasOne(d => d.User).WithMany(p => p.GroupMembers).HasConstraintName("group_members_user_id_fkey");
        });

        modelBuilder.Entity<GroupPost>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("group_posts_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Author).WithMany(p => p.GroupPosts).HasConstraintName("group_posts_author_id_fkey");

            entity.HasOne(d => d.Group).WithMany(p => p.GroupPosts).HasConstraintName("group_posts_group_id_fkey");
        });

        modelBuilder.Entity<InternalMark>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("internal_marks_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.Assignment).WithMany(p => p.InternalMarks)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("internal_marks_assignment_id_fkey");

            entity.HasOne(d => d.PublishedByNavigation).WithMany(p => p.InternalMarkPublishedByNavigations)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("internal_marks_published_by_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.InternalMarkStudents).HasConstraintName("internal_marks_student_id_fkey");

            entity.HasOne(d => d.Subject).WithMany(p => p.InternalMarks).HasConstraintName("internal_marks_subject_id_fkey");
        });

        modelBuilder.Entity<Material>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("materials_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.UploadedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Group).WithMany(p => p.Materials)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("materials_group_id_fkey");

            entity.HasOne(d => d.Subject).WithMany(p => p.Materials)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("materials_subject_id_fkey");

            entity.HasOne(d => d.UploadedByNavigation).WithMany(p => p.Materials)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("materials_uploaded_by_fkey");
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("messages_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.SentAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Sender).WithMany(p => p.Messages)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("messages_sender_id_fkey");

            entity.HasOne(d => d.Thread).WithMany(p => p.Messages).HasConstraintName("messages_thread_id_fkey");
        });

        modelBuilder.Entity<MessageThread>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("message_threads_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Student).WithMany(p => p.MessageThreadStudents).HasConstraintName("message_threads_student_id_fkey");

            entity.HasOne(d => d.Teacher).WithMany(p => p.MessageThreadTeachers).HasConstraintName("message_threads_teacher_id_fkey");
        });

        modelBuilder.Entity<Note>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("notes_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ContentMarkdown).HasDefaultValueSql("''::text");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Owner).WithMany(p => p.Notes).HasConstraintName("notes_owner_id_fkey");
        });

        modelBuilder.Entity<NoteLink>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("note_links_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.FromNote).WithMany(p => p.NoteLinkFromNotes).HasConstraintName("note_links_from_note_id_fkey");

            entity.HasOne(d => d.ToNote).WithMany(p => p.NoteLinkToNotes).HasConstraintName("note_links_to_note_id_fkey");
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("notifications_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Payload).HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Type).HasColumnType("notification_type");

            entity.HasOne(d => d.Recipient).WithMany(p => p.Notifications).HasConstraintName("notifications_recipient_id_fkey");
        });

        modelBuilder.Entity<ParentWard>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("parent_wards_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.ParentUser).WithMany(p => p.ParentWardParentUsers).HasConstraintName("parent_wards_parent_user_id_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.ParentWardStudents).HasConstraintName("parent_wards_student_id_fkey");
        });

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("payment_transactions_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ProcessedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Status).HasColumnType("payment_transaction_status");

            entity.HasOne(d => d.FeeRecord).WithMany(p => p.PaymentTransactions).HasConstraintName("payment_transactions_fee_record_id_fkey");
        });

        modelBuilder.Entity<Permission>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("permissions_pkey");
        });

        modelBuilder.Entity<PermissionGrant>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("permission_grants_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.GrantedByNavigation).WithMany(p => p.PermissionGrantGrantedByNavigations)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("permission_grants_granted_by_fkey");

            entity.HasOne(d => d.PermissionCodeNavigation).WithMany(p => p.PermissionGrants).HasConstraintName("permission_grants_permission_code_fkey");

            entity.HasOne(d => d.User).WithMany(p => p.PermissionGrantUsers).HasConstraintName("permission_grants_user_id_fkey");
        });

        modelBuilder.Entity<PlagiarismReport>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("plagiarism_reports_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CheckedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Submission).WithMany(p => p.PlagiarismReports).HasConstraintName("plagiarism_reports_submission_id_fkey");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("roles_pkey");

            entity.Property(e => e.DefaultScopeKind).HasColumnType("scope_kind");

            entity.HasMany(d => d.PermissionCodes).WithMany(p => p.RoleCodes)
                .UsingEntity<Dictionary<string, object>>(
                    "RoleDefaultPermission",
                    r => r.HasOne<Permission>().WithMany()
                        .HasForeignKey("PermissionCode")
                        .HasConstraintName("role_default_permissions_permission_code_fkey"),
                    l => l.HasOne<Role>().WithMany()
                        .HasForeignKey("RoleCode")
                        .HasConstraintName("role_default_permissions_role_code_fkey"),
                    j =>
                    {
                        j.HasKey("RoleCode", "PermissionCode").HasName("role_default_permissions_pkey");
                        j.ToTable("role_default_permissions");
                        j.IndexerProperty<string>("RoleCode").HasColumnName("role_code");
                        j.IndexerProperty<string>("PermissionCode").HasColumnName("permission_code");
                    });
        });

        modelBuilder.Entity<RoleBinding>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("role_bindings_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.GrantedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.ScopeType).HasColumnType("scope_kind");

            entity.HasOne(d => d.Department).WithMany(p => p.RoleBindings)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("role_bindings_department_id_fkey");

            entity.HasOne(d => d.RoleCodeNavigation).WithMany(p => p.RoleBindings)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("role_bindings_role_code_fkey");

            entity.HasOne(d => d.User).WithMany(p => p.RoleBindings).HasConstraintName("role_bindings_user_id_fkey");
        });

        modelBuilder.Entity<Section>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("sections_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.Department).WithMany(p => p.Sections).HasConstraintName("sections_department_id_fkey");
        });

        modelBuilder.Entity<SectionEnrollment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("section_enrollments_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.Section).WithMany(p => p.SectionEnrollments).HasConstraintName("section_enrollments_section_id_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.SectionEnrollments).HasConstraintName("section_enrollments_student_id_fkey");
        });

        modelBuilder.Entity<SectionFeedback>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("section_feedback_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.SubmittedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Section).WithMany(p => p.SectionFeedbacks).HasConstraintName("section_feedback_section_id_fkey");

            entity.HasOne(d => d.Teacher).WithMany(p => p.SectionFeedbacks).HasConstraintName("section_feedback_teacher_id_fkey");
        });

        modelBuilder.Entity<Subject>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("subjects_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.Department).WithMany(p => p.Subjects).HasConstraintName("subjects_department_id_fkey");

            entity.HasOne(d => d.Teacher).WithMany(p => p.Subjects)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("subjects_teacher_id_fkey");
        });

        modelBuilder.Entity<Submission>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("submissions_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.SubmittedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Assignment).WithMany(p => p.Submissions).HasConstraintName("submissions_assignment_id_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.Submissions).HasConstraintName("submissions_student_id_fkey");
        });

        modelBuilder.Entity<SuspiciousFlag>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("suspicious_flags_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.FlaggedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Assignment).WithMany(p => p.SuspiciousFlags)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("suspicious_flags_assignment_id_fkey");

            entity.HasOne(d => d.ClassSession).WithMany(p => p.SuspiciousFlags)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("suspicious_flags_class_session_id_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.SuspiciousFlags).HasConstraintName("suspicious_flags_student_id_fkey");
        });

        modelBuilder.Entity<TeacherFeedback>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("teacher_feedback_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.SubmittedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Student).WithMany(p => p.TeacherFeedbackStudents).HasConstraintName("teacher_feedback_student_id_fkey");

            entity.HasOne(d => d.Teacher).WithMany(p => p.TeacherFeedbackTeachers).HasConstraintName("teacher_feedback_teacher_id_fkey");
        });

        modelBuilder.Entity<TeacherReport>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("teacher_reports_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.SubmittedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Section).WithMany(p => p.TeacherReports)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("teacher_reports_section_id_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.TeacherReportStudents)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("teacher_reports_student_id_fkey");

            entity.HasOne(d => d.Teacher).WithMany(p => p.TeacherReportTeachers).HasConstraintName("teacher_reports_teacher_id_fkey");
        });

        modelBuilder.Entity<TeacherSectionAssignment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("teacher_section_assignments_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.Section).WithMany(p => p.TeacherSectionAssignments).HasConstraintName("teacher_section_assignments_section_id_fkey");

            entity.HasOne(d => d.Subject).WithMany(p => p.TeacherSectionAssignments).HasConstraintName("teacher_section_assignments_subject_id_fkey");

            entity.HasOne(d => d.Teacher).WithMany(p => p.TeacherSectionAssignments).HasConstraintName("teacher_section_assignments_teacher_id_fkey");
        });

        modelBuilder.Entity<TimetableChangeRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("timetable_change_requests_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.RequestedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.Status).HasColumnType("timetable_change_request_status").HasDefaultValueSql("'pending'::timetable_change_request_status");

            entity.HasOne(d => d.ReviewedByNavigation).WithMany(p => p.TimetableChangeRequestReviewedByNavigations)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("timetable_change_requests_reviewed_by_fkey");

            entity.HasOne(d => d.Teacher).WithMany(p => p.TimetableChangeRequestTeachers).HasConstraintName("timetable_change_requests_teacher_id_fkey");
        });

        modelBuilder.Entity<TimetableSlot>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("timetable_slots_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.HasOne(d => d.Section).WithMany(p => p.TimetableSlots).HasConstraintName("timetable_slots_section_id_fkey");

            entity.HasOne(d => d.Subject).WithMany(p => p.TimetableSlots)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("timetable_slots_subject_id_fkey");

            entity.HasOne(d => d.Teacher).WithMany(p => p.TimetableSlots)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("timetable_slots_teacher_id_fkey");
        });

        modelBuilder.Entity<Todo>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("todos_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Priority).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.StudentId, e.Completed }, "idx_todos_student");

            entity.HasOne(d => d.Student).WithMany(p => p.Todos).HasConstraintName("todos_student_id_fkey");
        });

        modelBuilder.Entity<UsageTelemetry>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("usage_telemetry_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Metadata).HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.RecordedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Assignment).WithMany(p => p.UsageTelemetries)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("usage_telemetry_assignment_id_fkey");

            entity.HasOne(d => d.ClassSession).WithMany(p => p.UsageTelemetries)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("usage_telemetry_class_session_id_fkey");

            entity.HasOne(d => d.Student).WithMany(p => p.UsageTelemetries).HasConstraintName("usage_telemetry_student_id_fkey");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("users_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.AccountType).HasColumnType("account_type");

            entity.HasOne(d => d.College).WithMany(p => p.Users)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("users_college_id_fkey");

            entity.HasOne(d => d.Department).WithMany(p => p.Users)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("users_department_id_fkey");
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("user_sessions_pkey");

            entity.HasIndex(e => e.UserId, "uniq_user_active_session")
                .IsUnique()
                .HasFilter("(is_active = true)");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.IsActive).HasDefaultValue(true);

            // #92 — one user can have many historical (inactive) session rows; only the
            // partial unique index (is_active = true) enforces "one active session" at the
            // DB level, so this must be a one-to-many, not one-to-one. Also load-bearing for
            // #130/#132's session-revocation work.
            entity.HasOne(d => d.User).WithMany(p => p.UserSessions).HasConstraintName("user_sessions_user_id_fkey");
        });

        modelBuilder.Entity<WhitelistRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("whitelist_requests_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Status).HasColumnType("whitelist_request_status");

            entity.HasOne(d => d.RequestedByNavigation).WithMany(p => p.WhitelistRequestRequestedByNavigations).HasConstraintName("whitelist_requests_requested_by_fkey");

            entity.HasOne(d => d.ReviewedByNavigation).WithMany(p => p.WhitelistRequestReviewedByNavigations)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("whitelist_requests_reviewed_by_fkey");
        });

        modelBuilder.Entity<WhitelistSite>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("whitelist_sites_pkey");

            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ApprovedAt).HasDefaultValueSql("now()");

            entity.HasOne(d => d.College).WithMany(p => p.WhitelistSites).HasConstraintName("whitelist_sites_college_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
