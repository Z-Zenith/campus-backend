using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("exam_schedules")]
[Index("SectionId", "SubjectId", "ExamType", Name = "exam_schedules_section_id_subject_id_exam_type_key", IsUnique = true)]
public partial class ExamSchedule
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("section_id")]
    public Guid SectionId { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    [Column("exam_type")]
    public ExamType ExamType { get; set; }

    [Column("exam_date")]
    public DateOnly ExamDate { get; set; }

    [Column("start_time")]
    public TimeOnly StartTime { get; set; }

    [Column("end_time")]
    public TimeOnly EndTime { get; set; }

    [Column("room")]
    public string? Room { get; set; }

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [ForeignKey("SectionId")]
    [InverseProperty("ExamSchedules")]
    public virtual Section Section { get; set; } = null!;

    [ForeignKey("SubjectId")]
    [InverseProperty("ExamSchedules")]
    public virtual Subject Subject { get; set; } = null!;

    [ForeignKey("CreatedBy")]
    [InverseProperty("ExamSchedules")]
    public virtual User CreatedByNavigation { get; set; } = null!;
}
