using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("events")]
[Index("CollegeId", "StartTime", Name = "idx_events_college_time")]
public partial class Event
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("college_id")]
    public Guid CollegeId { get; set; }

    [Column("title")]
    public string Title { get; set; } = null!;

    [Column("start_time")]
    public DateTime StartTime { get; set; }

    [Column("end_time")]
    public DateTime EndTime { get; set; }

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [ForeignKey("CollegeId")]
    [InverseProperty("Events")]
    public virtual College College { get; set; } = null!;

    [ForeignKey("CreatedBy")]
    [InverseProperty("Events")]
    public virtual User CreatedByNavigation { get; set; } = null!;

    [InverseProperty("Event")]
    public virtual ICollection<EventRegistration> EventRegistrations { get; set; } = new List<EventRegistration>();

    // SQL Server has no array column type — these were `int[]`/`uuid[]` columns under
    // Postgres (see MIGRATIONS.md / db/init/01_schema.sql history) and are now junction
    // tables so CalendarController's EligibleEventsQuery still translates to SQL instead of
    // throwing on an unsupported client-eval. Null-vs-empty is no longer distinguishable
    // (both now mean "no restriction on this dimension") — the prior null-check "restricted to
    // nothing" edge case was never exercised by any caller (CreateEventRequest only ever sends
    // null or a real list), so this is treated as an accepted behavior change, not a bug.
    [InverseProperty("Event")]
    public virtual ICollection<EventRestrictedYear> RestrictedYears { get; set; } = new List<EventRestrictedYear>();

    [InverseProperty("Event")]
    public virtual ICollection<EventRestrictedDepartment> RestrictedDepartments { get; set; } = new List<EventRestrictedDepartment>();
}
