using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("assignments")]
public partial class Assignment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    [Column("teacher_id")]
    public Guid TeacherId { get; set; }

    [Column("title")]
    public string Title { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

    [Column("due_date")]
    public DateTime DueDate { get; set; }

    [Column("submission_window_start")]
    public DateTime SubmissionWindowStart { get; set; }

    [Column("submission_window_end")]
    public DateTime SubmissionWindowEnd { get; set; }

    [Column("type_specific_settings", TypeName = "nvarchar(max)")]
    public string? TypeSpecificSettings { get; set; }

    [InverseProperty("Assignment")]
    public virtual ICollection<InternalMark> InternalMarks { get; set; } = new List<InternalMark>();

    [ForeignKey("SubjectId")]
    [InverseProperty("Assignments")]
    public virtual Subject Subject { get; set; } = null!;

    [InverseProperty("Assignment")]
    public virtual ICollection<Submission> Submissions { get; set; } = new List<Submission>();

    [InverseProperty("Assignment")]
    public virtual ICollection<SuspiciousFlag> SuspiciousFlags { get; set; } = new List<SuspiciousFlag>();

    [ForeignKey("TeacherId")]
    [InverseProperty("Assignments")]
    public virtual User Teacher { get; set; } = null!;

    [InverseProperty("Assignment")]
    public virtual ICollection<UsageTelemetry> UsageTelemetries { get; set; } = new List<UsageTelemetry>();
}
