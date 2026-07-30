using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("regulation_subject_offerings")]
[Index("RegulationId", "SubjectId", Name = "regulation_subject_offerings_regulation_id_subject_id_key", IsUnique = true)]
public partial class RegulationSubjectOffering
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("regulation_id")]
    public Guid RegulationId { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    [Column("semester")]
    public int Semester { get; set; }

    [Column("lecture_hours")]
    public int LectureHours { get; set; }

    [Column("tutorial_hours")]
    public int TutorialHours { get; set; }

    [Column("practical_hours")]
    public int PracticalHours { get; set; }

    [Column("credits", TypeName = "numeric(3,1)")]
    public decimal Credits { get; set; }

    [Column("is_elective")]
    public bool IsElective { get; set; }

    [Column("is_lab")]
    public bool IsLab { get; set; }

    [Column("min_attendance_percent", TypeName = "numeric(4,1)")]
    public decimal MinAttendancePercent { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [ForeignKey("RegulationId")]
    [InverseProperty("RegulationSubjectOfferings")]
    public virtual Regulation Regulation { get; set; } = null!;

    [ForeignKey("SubjectId")]
    [InverseProperty("RegulationSubjectOfferings")]
    public virtual Subject Subject { get; set; } = null!;

    [InverseProperty("Offering")]
    public virtual ICollection<CurriculumUnit> CurriculumUnits { get; set; } = new List<CurriculumUnit>();
}
