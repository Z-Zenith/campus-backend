using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("sections")]
[Index("DepartmentId", "Year", Name = "idx_sections_dept_year")]
public partial class Section
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("department_id")]
    public Guid DepartmentId { get; set; }

    [Column("year")]
    public int Year { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    [ForeignKey("DepartmentId")]
    [InverseProperty("Sections")]
    public virtual Department Department { get; set; } = null!;

    [InverseProperty("Section")]
    public virtual ICollection<ExamSchedule> ExamSchedules { get; set; } = new List<ExamSchedule>();

    [InverseProperty("Section")]
    public virtual ICollection<Group> Groups { get; set; } = new List<Group>();

    [InverseProperty("Section")]
    public virtual ICollection<SectionEnrollment> SectionEnrollments { get; set; } = new List<SectionEnrollment>();

    [InverseProperty("Section")]
    public virtual ICollection<SectionFeedback> SectionFeedbacks { get; set; } = new List<SectionFeedback>();

    [InverseProperty("Section")]
    public virtual ICollection<TeacherReport> TeacherReports { get; set; } = new List<TeacherReport>();

    [InverseProperty("Section")]
    public virtual ICollection<TeacherSectionAssignment> TeacherSectionAssignments { get; set; } = new List<TeacherSectionAssignment>();

    [InverseProperty("Section")]
    public virtual ICollection<TimetableSlot> TimetableSlots { get; set; } = new List<TimetableSlot>();
}
