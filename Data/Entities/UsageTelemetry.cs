using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("usage_telemetry")]
[Index("StudentId", "RecordedAt", Name = "idx_usage_telemetry_student_time", IsDescending = new[] { false, true })]
public partial class UsageTelemetry
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("student_id")]
    public Guid StudentId { get; set; }

    [Column("class_session_id")]
    public Guid? ClassSessionId { get; set; }

    [Column("assignment_id")]
    public Guid? AssignmentId { get; set; }

    [Column("event_type")]
    public string EventType { get; set; } = null!;

    [Column("metadata", TypeName = "nvarchar(max)")]
    public string Metadata { get; set; } = null!;

    [Column("recorded_at")]
    public DateTime RecordedAt { get; set; }

    [ForeignKey("AssignmentId")]
    [InverseProperty("UsageTelemetries")]
    public virtual Assignment? Assignment { get; set; }

    [ForeignKey("ClassSessionId")]
    [InverseProperty("UsageTelemetries")]
    public virtual ClassSession? ClassSession { get; set; }

    [ForeignKey("StudentId")]
    [InverseProperty("UsageTelemetries")]
    public virtual User Student { get; set; } = null!;
}
