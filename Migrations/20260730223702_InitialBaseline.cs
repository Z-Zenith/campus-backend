using System;
using System.Collections.Generic;
using BackendApi.Data.Entities;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendApi.Migrations
{
    /// <inheritdoc />
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:account_type", "student,teacher,admin_tier,parent")
                .Annotation("Npgsql:Enum:assignment_type", "code,quiz,essay,file_upload")
                .Annotation("Npgsql:Enum:attendance_status", "present,absent,late")
                .Annotation("Npgsql:Enum:doc_type", "pdf,pptx,docx")
                .Annotation("Npgsql:Enum:fee_status", "pending,paid")
                .Annotation("Npgsql:Enum:group_type", "class,subject_section,club,teacher_only")
                .Annotation("Npgsql:Enum:notification_type", "exit_ping,absence_ping,report,timetable_request,fee_reminder,whitelist_request,suspicious_flag")
                .Annotation("Npgsql:Enum:ocr_status", "pending,processing,completed,failed,not_applicable")
                .Annotation("Npgsql:Enum:scope_kind", "global,department")
                .Annotation("Npgsql:Enum:whitelist_request_status", "pending,approved,rejected")
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,");

            migrationBuilder.CreateTable(
                name: "colleges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "text", nullable: false),
                    time_zone = table.Column<string>(type: "text", nullable: false, defaultValue: "UTC"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("colleges_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("permissions_pkey", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    default_scope_kind = table.Column<ScopeKind>(type: "scope_kind", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("roles_pkey", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "whitelist_sites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    college_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("whitelist_sites_pkey", x => x.id);
                    table.ForeignKey(
                        name: "whitelist_sites_college_id_fkey",
                        column: x => x.college_id,
                        principalTable: "colleges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_default_permissions",
                columns: table => new
                {
                    role_code = table.Column<string>(type: "text", nullable: false),
                    permission_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("role_default_permissions_pkey", x => new { x.role_code, x.permission_code });
                    table.ForeignKey(
                        name: "role_default_permissions_permission_code_fkey",
                        column: x => x.permission_code,
                        principalTable: "permissions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "role_default_permissions_role_code_fkey",
                        column: x => x.role_code,
                        principalTable: "roles",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_detection_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_likelihood_score = table.Column<decimal>(type: "numeric", nullable: false),
                    pangram_report_id = table.Column<string>(type: "text", nullable: true),
                    checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("ai_detection_reports_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    due_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submission_window_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submission_window_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    type_specific_settings = table.Column<string>(type: "jsonb", nullable: true),
                    type = table.Column<AssignmentType>(type: "assignment_type", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("assignments_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attendance_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    class_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    marked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    marked_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<AttendanceStatus>(type: "attendance_status", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("attendance_records_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "autograde_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    suggested_grade = table.Column<decimal>(type: "numeric", nullable: false),
                    confidence = table.Column<decimal>(type: "numeric", nullable: true),
                    matched_criteria = table.Column<string>(type: "jsonb", nullable: true),
                    feedback = table.Column<string>(type: "jsonb", nullable: true),
                    confirmed_by_teacher = table.Column<bool>(type: "boolean", nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("autograde_suggestions_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "browsing_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    visited_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("browsing_history_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "browsing_history_summaries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    summary_text = table.Column<string>(type: "text", nullable: false),
                    generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("browsing_history_summaries_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "class_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    timetable_slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_date = table.Column<DateOnly>(type: "date", nullable: false),
                    actual_teacher_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("class_sessions_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "code_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false, defaultValueSql: "''::text"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("code_files_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "code_projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    entry_file_path = table.Column<string>(type: "text", nullable: false),
                    active_file_path = table.Column<string>(type: "text", nullable: false),
                    stdin = table.Column<string>(type: "text", nullable: false, defaultValueSql: "''::text"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("code_projects_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "copy_check_flags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    submission_a_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_b_id = table.Column<Guid>(type: "uuid", nullable: false),
                    similarity_score = table.Column<decimal>(type: "numeric", nullable: false),
                    flagged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("copy_check_flags_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "custom_calendar_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    entry_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("custom_calendar_entries_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    college_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    hod_role_binding_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("departments_pkey", x => x.id);
                    table.ForeignKey(
                        name: "departments_college_id_fkey",
                        column: x => x.college_id,
                        principalTable: "colleges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("sections_pkey", x => x.id);
                    table.ForeignKey(
                        name: "sections_department_id_fkey",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    college_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identifier = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    totp_secret = table.Column<string>(type: "text", nullable: true),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    account_type = table.Column<AccountType>(type: "account_type", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("users_pkey", x => x.id);
                    table.ForeignKey(
                        name: "users_college_id_fkey",
                        column: x => x.college_id,
                        principalTable: "colleges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "users_department_id_fkey",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_url = table.Column<string>(type: "text", nullable: false),
                    annotations = table.Column<string>(type: "jsonb", nullable: true),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    doc_type = table.Column<DocType>(type: "doc_type", nullable: false),
                    ocr_status = table.Column<int>(type: "ocr_status", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("documents_pkey", x => x.id);
                    table.ForeignKey(
                        name: "documents_owner_id_fkey",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    college_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    start_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    end_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    restricted_years = table.Column<List<int>>(type: "integer[]", nullable: true),
                    restricted_departments = table.Column<List<Guid>>(type: "uuid[]", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("events_pkey", x => x.id);
                    table.ForeignKey(
                        name: "events_college_id_fkey",
                        column: x => x.college_id,
                        principalTable: "colleges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "events_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fee_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    payment_link = table.Column<string>(type: "text", nullable: true),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<FeeStatus>(type: "fee_status", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("fee_records_pkey", x => x.id);
                    table.ForeignKey(
                        name: "fee_records_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    college_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<GroupType>(type: "group_type", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("groups_pkey", x => x.id);
                    table.ForeignKey(
                        name: "groups_college_id_fkey",
                        column: x => x.college_id,
                        principalTable: "colleges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "groups_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "groups_section_id_fkey",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "message_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("message_threads_pkey", x => x.id);
                    table.ForeignKey(
                        name: "message_threads_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "message_threads_teacher_id_fkey",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    content_markdown = table.Column<string>(type: "text", nullable: false, defaultValueSql: "''::text"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("notes_pkey", x => x.id);
                    table.ForeignKey(
                        name: "notes_owner_id_fkey",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    type = table.Column<NotificationType>(type: "notification_type", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("notifications_pkey", x => x.id);
                    table.ForeignKey(
                        name: "notifications_recipient_id_fkey",
                        column: x => x.recipient_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "parent_wards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    parent_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("parent_wards_pkey", x => x.id);
                    table.ForeignKey(
                        name: "parent_wards_parent_user_id_fkey",
                        column: x => x.parent_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "parent_wards_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "permission_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_code = table.Column<string>(type: "text", nullable: false),
                    granted = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("permission_grants_pkey", x => x.id);
                    table.ForeignKey(
                        name: "permission_grants_granted_by_fkey",
                        column: x => x.granted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "permission_grants_permission_code_fkey",
                        column: x => x.permission_code,
                        principalTable: "permissions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "permission_grants_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_bindings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_code = table.Column<string>(type: "text", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    scope_type = table.Column<ScopeKind>(type: "scope_kind", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("role_bindings_pkey", x => x.id);
                    table.ForeignKey(
                        name: "role_bindings_department_id_fkey",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "role_bindings_role_code_fkey",
                        column: x => x.role_code,
                        principalTable: "roles",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "role_bindings_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "section_enrollments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("section_enrollments_pkey", x => x.id);
                    table.ForeignKey(
                        name: "section_enrollments_section_id_fkey",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "section_enrollments_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "section_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("section_feedback_pkey", x => x.id);
                    table.ForeignKey(
                        name: "section_feedback_section_id_fkey",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "section_feedback_teacher_id_fkey",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subjects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("subjects_pkey", x => x.id);
                    table.ForeignKey(
                        name: "subjects_department_id_fkey",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "subjects_teacher_id_fkey",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "submissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_url = table.Column<string>(type: "text", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    is_late = table.Column<bool>(type: "boolean", nullable: false),
                    is_autosubmitted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("submissions_pkey", x => x.id);
                    table.ForeignKey(
                        name: "submissions_assignment_id_fkey",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "submissions_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "suspicious_flags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confidence_score = table.Column<decimal>(type: "numeric", nullable: false),
                    flagged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("suspicious_flags_pkey", x => x.id);
                    table.ForeignKey(
                        name: "suspicious_flags_assignment_id_fkey",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "suspicious_flags_class_session_id_fkey",
                        column: x => x.class_session_id,
                        principalTable: "class_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "suspicious_flags_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "teacher_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    comments = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("teacher_feedback_pkey", x => x.id);
                    table.ForeignKey(
                        name: "teacher_feedback_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "teacher_feedback_teacher_id_fkey",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "teacher_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    student_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("teacher_reports_pkey", x => x.id);
                    table.ForeignKey(
                        name: "teacher_reports_section_id_fkey",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "teacher_reports_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "teacher_reports_teacher_id_fkey",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timetable_change_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'pending'::text"),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("timetable_change_requests_pkey", x => x.id);
                    table.ForeignKey(
                        name: "timetable_change_requests_reviewed_by_fkey",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "timetable_change_requests_teacher_id_fkey",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "todos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    due_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed = table.Column<bool>(type: "boolean", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("todos_pkey", x => x.id);
                    table.ForeignKey(
                        name: "todos_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "usage_telemetry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("usage_telemetry_pkey", x => x.id);
                    table.ForeignKey(
                        name: "usage_telemetry_assignment_id_fkey",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "usage_telemetry_class_session_id_fkey",
                        column: x => x.class_session_id,
                        principalTable: "class_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "usage_telemetry_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_info = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("user_sessions_pkey", x => x.id);
                    table.ForeignKey(
                        name: "user_sessions_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "whitelist_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    url = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<WhitelistRequestStatus>(type: "whitelist_request_status", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("whitelist_requests_pkey", x => x.id);
                    table.ForeignKey(
                        name: "whitelist_requests_requested_by_fkey",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "whitelist_requests_reviewed_by_fkey",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "event_registrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("event_registrations_pkey", x => x.id);
                    table.ForeignKey(
                        name: "event_registrations_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "event_registrations_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    fee_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway_txn_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("payment_transactions_pkey", x => x.id);
                    table.ForeignKey(
                        name: "payment_transactions_fee_record_id_fkey",
                        column: x => x.fee_record_id,
                        principalTable: "fee_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("group_members_pkey", x => x.id);
                    table.ForeignKey(
                        name: "group_members_group_id_fkey",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "group_members_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_posts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("group_posts_pkey", x => x.id);
                    table.ForeignKey(
                        name: "group_posts_author_id_fkey",
                        column: x => x.author_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "group_posts_group_id_fkey",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("messages_pkey", x => x.id);
                    table.ForeignKey(
                        name: "messages_sender_id_fkey",
                        column: x => x.sender_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "messages_thread_id_fkey",
                        column: x => x.thread_id,
                        principalTable: "message_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "note_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    from_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anchor = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("note_links_pkey", x => x.id);
                    table.ForeignKey(
                        name: "note_links_from_note_id_fkey",
                        column: x => x.from_note_id,
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "note_links_to_note_id_fkey",
                        column: x => x.to_note_id,
                        principalTable: "notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "external_marks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grade = table.Column<string>(type: "text", nullable: false),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    approved = table.Column<bool>(type: "boolean", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    published = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("external_marks_pkey", x => x.id);
                    table.ForeignKey(
                        name: "external_marks_approved_by_fkey",
                        column: x => x.approved_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "external_marks_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "external_marks_subject_id_fkey",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "external_marks_submitted_by_fkey",
                        column: x => x.submitted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "internal_marks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    marks = table.Column<decimal>(type: "numeric", nullable: false),
                    published = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("internal_marks_pkey", x => x.id);
                    table.ForeignKey(
                        name: "internal_marks_assignment_id_fkey",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "internal_marks_published_by_fkey",
                        column: x => x.published_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "internal_marks_student_id_fkey",
                        column: x => x.student_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "internal_marks_subject_id_fkey",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "materials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    file_url = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("materials_pkey", x => x.id);
                    table.ForeignKey(
                        name: "materials_group_id_fkey",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "materials_subject_id_fkey",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "materials_uploaded_by_fkey",
                        column: x => x.uploaded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "teacher_section_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("teacher_section_assignments_pkey", x => x.id);
                    table.ForeignKey(
                        name: "teacher_section_assignments_section_id_fkey",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "teacher_section_assignments_subject_id_fkey",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "teacher_section_assignments_teacher_id_fkey",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timetable_slots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    teacher_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    room = table.Column<string>(type: "text", nullable: true),
                    manually_edited = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("timetable_slots_pkey", x => x.id);
                    table.ForeignKey(
                        name: "timetable_slots_section_id_fkey",
                        column: x => x.section_id,
                        principalTable: "sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "timetable_slots_subject_id_fkey",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "timetable_slots_teacher_id_fkey",
                        column: x => x.teacher_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plagiarism_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    similarity_score = table.Column<decimal>(type: "numeric", nullable: false),
                    copyleaks_scan_id = table.Column<string>(type: "text", nullable: true),
                    matched_sources = table.Column<string>(type: "jsonb", nullable: true),
                    checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("plagiarism_reports_pkey", x => x.id);
                    table.ForeignKey(
                        name: "plagiarism_reports_submission_id_fkey",
                        column: x => x.submission_id,
                        principalTable: "submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_detection_reports_submission_id",
                table: "ai_detection_reports",
                column: "submission_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_subject_id",
                table: "assignments",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignments_teacher_id",
                table: "assignments",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "attendance_records_class_session_id_student_id_key",
                table: "attendance_records",
                columns: new[] { "class_session_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_records_marked_by",
                table: "attendance_records",
                column: "marked_by");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_records_student_id",
                table: "attendance_records",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_autograde_suggestions_submission_id",
                table: "autograde_suggestions",
                column: "submission_id");

            migrationBuilder.CreateIndex(
                name: "idx_browsing_history_student_time",
                table: "browsing_history",
                columns: new[] { "student_id", "visited_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_browsing_history_summaries_student_id",
                table: "browsing_history_summaries",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "class_sessions_timetable_slot_id_session_date_key",
                table: "class_sessions",
                columns: new[] { "timetable_slot_id", "session_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_class_sessions_actual_teacher_id",
                table: "class_sessions",
                column: "actual_teacher_id");

            migrationBuilder.CreateIndex(
                name: "code_files_project_id_path_key",
                table: "code_files",
                columns: new[] { "project_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_code_files_project",
                table: "code_files",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "idx_code_projects_owner_updated",
                table: "code_projects",
                columns: new[] { "owner_id", "updated_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_copy_check_flags_submission_a_id",
                table: "copy_check_flags",
                column: "submission_a_id");

            migrationBuilder.CreateIndex(
                name: "IX_copy_check_flags_submission_b_id",
                table: "copy_check_flags",
                column: "submission_b_id");

            migrationBuilder.CreateIndex(
                name: "idx_custom_calendar_student",
                table: "custom_calendar_entries",
                columns: new[] { "student_id", "entry_date" });

            migrationBuilder.CreateIndex(
                name: "IX_departments_college_id",
                table: "departments",
                column: "college_id");

            migrationBuilder.CreateIndex(
                name: "IX_departments_hod_role_binding_id",
                table: "departments",
                column: "hod_role_binding_id");

            migrationBuilder.CreateIndex(
                name: "IX_documents_owner_id",
                table: "documents",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "event_registrations_event_id_student_id_key",
                table: "event_registrations",
                columns: new[] { "event_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_registrations_student_id",
                table: "event_registrations",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "idx_events_college_time",
                table: "events",
                columns: new[] { "college_id", "start_time" });

            migrationBuilder.CreateIndex(
                name: "IX_events_created_by",
                table: "events",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_external_marks_approved_by",
                table: "external_marks",
                column: "approved_by");

            migrationBuilder.CreateIndex(
                name: "IX_external_marks_student_id",
                table: "external_marks",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_marks_subject_id",
                table: "external_marks",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_marks_submitted_by",
                table: "external_marks",
                column: "submitted_by");

            migrationBuilder.CreateIndex(
                name: "IX_fee_records_student_id",
                table: "fee_records",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "group_members_group_id_user_id_key",
                table: "group_members",
                columns: new[] { "group_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_members_user_id",
                table: "group_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_group_posts_group",
                table: "group_posts",
                columns: new[] { "group_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_group_posts_author_id",
                table: "group_posts",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "idx_groups_college",
                table: "groups",
                column: "college_id");

            migrationBuilder.CreateIndex(
                name: "IX_groups_created_by",
                table: "groups",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_groups_section_id",
                table: "groups",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "idx_internal_marks_student_subject",
                table: "internal_marks",
                columns: new[] { "student_id", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "IX_internal_marks_assignment_id",
                table: "internal_marks",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "IX_internal_marks_published_by",
                table: "internal_marks",
                column: "published_by");

            migrationBuilder.CreateIndex(
                name: "IX_internal_marks_subject_id",
                table: "internal_marks",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_materials_group_id",
                table: "materials",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "IX_materials_subject_id",
                table: "materials",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_materials_uploaded_by",
                table: "materials",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "IX_message_threads_teacher_id",
                table: "message_threads",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "message_threads_student_id_teacher_id_key",
                table: "message_threads",
                columns: new[] { "student_id", "teacher_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_messages_thread",
                table: "messages",
                columns: new[] { "thread_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "IX_messages_sender_id",
                table: "messages",
                column: "sender_id");

            migrationBuilder.CreateIndex(
                name: "IX_note_links_to_note_id",
                table: "note_links",
                column: "to_note_id");

            migrationBuilder.CreateIndex(
                name: "note_links_from_note_id_to_note_id_key",
                table: "note_links",
                columns: new[] { "from_note_id", "to_note_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notes_owner_id",
                table: "notes",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "idx_notifications_recipient",
                table: "notifications",
                columns: new[] { "recipient_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_parent_wards_student",
                table: "parent_wards",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "parent_wards_parent_user_id_student_id_key",
                table: "parent_wards",
                columns: new[] { "parent_user_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_transactions_fee_record_id",
                table: "payment_transactions",
                column: "fee_record_id");

            migrationBuilder.CreateIndex(
                name: "payment_transactions_gateway_txn_id_key",
                table: "payment_transactions",
                column: "gateway_txn_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_permission_grants_user",
                table: "permission_grants",
                columns: new[] { "user_id", "permission_code" });

            migrationBuilder.CreateIndex(
                name: "IX_permission_grants_granted_by",
                table: "permission_grants",
                column: "granted_by");

            migrationBuilder.CreateIndex(
                name: "IX_permission_grants_permission_code",
                table: "permission_grants",
                column: "permission_code");

            migrationBuilder.CreateIndex(
                name: "IX_plagiarism_reports_submission_id",
                table: "plagiarism_reports",
                column: "submission_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_bindings_department_id",
                table: "role_bindings",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_bindings_role_code",
                table: "role_bindings",
                column: "role_code");

            migrationBuilder.CreateIndex(
                name: "IX_role_bindings_user_id",
                table: "role_bindings",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_default_permissions_permission_code",
                table: "role_default_permissions",
                column: "permission_code");

            migrationBuilder.CreateIndex(
                name: "IX_section_enrollments_student_id",
                table: "section_enrollments",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "section_enrollments_section_id_student_id_key",
                table: "section_enrollments",
                columns: new[] { "section_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_section_feedback_section_id",
                table: "section_feedback",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "IX_section_feedback_teacher_id",
                table: "section_feedback",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "idx_sections_dept_year",
                table: "sections",
                columns: new[] { "department_id", "year" });

            migrationBuilder.CreateIndex(
                name: "IX_subjects_teacher_id",
                table: "subjects",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "subjects_department_id_code_key",
                table: "subjects",
                columns: new[] { "department_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_submissions_assignment",
                table: "submissions",
                columns: new[] { "assignment_id", "student_id" });

            migrationBuilder.CreateIndex(
                name: "IX_submissions_student_id",
                table: "submissions",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "idx_suspicious_flags_student",
                table: "suspicious_flags",
                columns: new[] { "student_id", "flagged_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_suspicious_flags_assignment_id",
                table: "suspicious_flags",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "IX_suspicious_flags_class_session_id",
                table: "suspicious_flags",
                column: "class_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_teacher_feedback_student_id",
                table: "teacher_feedback",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_teacher_feedback_teacher_id",
                table: "teacher_feedback",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "IX_teacher_reports_section_id",
                table: "teacher_reports",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "IX_teacher_reports_student_id",
                table: "teacher_reports",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_teacher_reports_teacher_id",
                table: "teacher_reports",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "IX_teacher_section_assignments_section_id",
                table: "teacher_section_assignments",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "IX_teacher_section_assignments_subject_id",
                table: "teacher_section_assignments",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "teacher_section_assignments_teacher_id_section_id_subject_i_key",
                table: "teacher_section_assignments",
                columns: new[] { "teacher_id", "section_id", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_timetable_change_requests_reviewed_by",
                table: "timetable_change_requests",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_timetable_change_requests_teacher_id",
                table: "timetable_change_requests",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "idx_timetable_slots_section",
                table: "timetable_slots",
                columns: new[] { "section_id", "day_of_week", "start_time" });

            migrationBuilder.CreateIndex(
                name: "IX_timetable_slots_subject_id",
                table: "timetable_slots",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_timetable_slots_teacher_id",
                table: "timetable_slots",
                column: "teacher_id");

            migrationBuilder.CreateIndex(
                name: "idx_todos_student",
                table: "todos",
                columns: new[] { "student_id", "completed" });

            migrationBuilder.CreateIndex(
                name: "idx_usage_telemetry_student_time",
                table: "usage_telemetry",
                columns: new[] { "student_id", "recorded_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_usage_telemetry_assignment_id",
                table: "usage_telemetry",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "IX_usage_telemetry_class_session_id",
                table: "usage_telemetry",
                column: "class_session_id");

            migrationBuilder.CreateIndex(
                name: "idx_user_sessions_user",
                table: "user_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "uniq_user_active_session",
                table: "user_sessions",
                column: "user_id",
                unique: true,
                filter: "(is_active = true)");

            migrationBuilder.CreateIndex(
                name: "IX_users_department_id",
                table: "users",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "users_college_id_identifier_key",
                table: "users",
                columns: new[] { "college_id", "identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_whitelist_requests_requested_by",
                table: "whitelist_requests",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "IX_whitelist_requests_reviewed_by",
                table: "whitelist_requests",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "whitelist_sites_college_id_url_key",
                table: "whitelist_sites",
                columns: new[] { "college_id", "url" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "ai_detection_reports_submission_id_fkey",
                table: "ai_detection_reports",
                column: "submission_id",
                principalTable: "submissions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "assignments_subject_id_fkey",
                table: "assignments",
                column: "subject_id",
                principalTable: "subjects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "assignments_teacher_id_fkey",
                table: "assignments",
                column: "teacher_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "attendance_records_class_session_id_fkey",
                table: "attendance_records",
                column: "class_session_id",
                principalTable: "class_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "attendance_records_marked_by_fkey",
                table: "attendance_records",
                column: "marked_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "attendance_records_student_id_fkey",
                table: "attendance_records",
                column: "student_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "autograde_suggestions_submission_id_fkey",
                table: "autograde_suggestions",
                column: "submission_id",
                principalTable: "submissions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "browsing_history_student_id_fkey",
                table: "browsing_history",
                column: "student_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "browsing_history_summaries_student_id_fkey",
                table: "browsing_history_summaries",
                column: "student_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "class_sessions_actual_teacher_id_fkey",
                table: "class_sessions",
                column: "actual_teacher_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "class_sessions_timetable_slot_id_fkey",
                table: "class_sessions",
                column: "timetable_slot_id",
                principalTable: "timetable_slots",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "code_files_project_id_fkey",
                table: "code_files",
                column: "project_id",
                principalTable: "code_projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "code_projects_owner_id_fkey",
                table: "code_projects",
                column: "owner_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "copy_check_flags_submission_a_id_fkey",
                table: "copy_check_flags",
                column: "submission_a_id",
                principalTable: "submissions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "copy_check_flags_submission_b_id_fkey",
                table: "copy_check_flags",
                column: "submission_b_id",
                principalTable: "submissions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "custom_calendar_entries_student_id_fkey",
                table: "custom_calendar_entries",
                column: "student_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "departments_hod_fk",
                table: "departments",
                column: "hod_role_binding_id",
                principalTable: "role_bindings",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "role_bindings_user_id_fkey",
                table: "role_bindings");

            migrationBuilder.DropForeignKey(
                name: "departments_college_id_fkey",
                table: "departments");

            migrationBuilder.DropForeignKey(
                name: "departments_hod_fk",
                table: "departments");

            migrationBuilder.DropTable(
                name: "ai_detection_reports");

            migrationBuilder.DropTable(
                name: "attendance_records");

            migrationBuilder.DropTable(
                name: "autograde_suggestions");

            migrationBuilder.DropTable(
                name: "browsing_history");

            migrationBuilder.DropTable(
                name: "browsing_history_summaries");

            migrationBuilder.DropTable(
                name: "code_files");

            migrationBuilder.DropTable(
                name: "copy_check_flags");

            migrationBuilder.DropTable(
                name: "custom_calendar_entries");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "event_registrations");

            migrationBuilder.DropTable(
                name: "external_marks");

            migrationBuilder.DropTable(
                name: "group_members");

            migrationBuilder.DropTable(
                name: "group_posts");

            migrationBuilder.DropTable(
                name: "internal_marks");

            migrationBuilder.DropTable(
                name: "materials");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "note_links");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "parent_wards");

            migrationBuilder.DropTable(
                name: "payment_transactions");

            migrationBuilder.DropTable(
                name: "permission_grants");

            migrationBuilder.DropTable(
                name: "plagiarism_reports");

            migrationBuilder.DropTable(
                name: "role_default_permissions");

            migrationBuilder.DropTable(
                name: "section_enrollments");

            migrationBuilder.DropTable(
                name: "section_feedback");

            migrationBuilder.DropTable(
                name: "suspicious_flags");

            migrationBuilder.DropTable(
                name: "teacher_feedback");

            migrationBuilder.DropTable(
                name: "teacher_reports");

            migrationBuilder.DropTable(
                name: "teacher_section_assignments");

            migrationBuilder.DropTable(
                name: "timetable_change_requests");

            migrationBuilder.DropTable(
                name: "todos");

            migrationBuilder.DropTable(
                name: "usage_telemetry");

            migrationBuilder.DropTable(
                name: "user_sessions");

            migrationBuilder.DropTable(
                name: "whitelist_requests");

            migrationBuilder.DropTable(
                name: "whitelist_sites");

            migrationBuilder.DropTable(
                name: "code_projects");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropTable(
                name: "message_threads");

            migrationBuilder.DropTable(
                name: "notes");

            migrationBuilder.DropTable(
                name: "fee_records");

            migrationBuilder.DropTable(
                name: "submissions");

            migrationBuilder.DropTable(
                name: "permissions");

            migrationBuilder.DropTable(
                name: "class_sessions");

            migrationBuilder.DropTable(
                name: "assignments");

            migrationBuilder.DropTable(
                name: "timetable_slots");

            migrationBuilder.DropTable(
                name: "sections");

            migrationBuilder.DropTable(
                name: "subjects");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "colleges");

            migrationBuilder.DropTable(
                name: "role_bindings");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}
