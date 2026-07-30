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

    [Column("restricted_years")]
    public List<int>? RestrictedYears { get; set; }

    [Column("restricted_departments")]
    public List<Guid>? RestrictedDepartments { get; set; }

    [Column("event_type")]
    public EventType EventType { get; set; }

    [Column("status")]
    public EventStatus Status { get; set; }

    [Column("approved_by")]
    public Guid? ApprovedBy { get; set; }

    [Column("approved_at")]
    public DateTime? ApprovedAt { get; set; }

    [Column("recurrence_rule")]
    public string? RecurrenceRule { get; set; }

    [ForeignKey("CollegeId")]
    [InverseProperty("Events")]
    public virtual College College { get; set; } = null!;

    [ForeignKey("CreatedBy")]
    [InverseProperty("Events")]
    public virtual User CreatedByNavigation { get; set; } = null!;

    [ForeignKey("ApprovedBy")]
    [InverseProperty("EventsApproved")]
    public virtual User? ApprovedByNavigation { get; set; }

    [InverseProperty("Event")]
    public virtual ICollection<EventRegistration> EventRegistrations { get; set; } = new List<EventRegistration>();
}
