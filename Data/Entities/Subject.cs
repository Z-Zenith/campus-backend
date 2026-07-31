using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("subjects")]
[Index("DepartmentId", "Code", Name = "subjects_department_id_code_key", IsUnique = true)]
public partial class Subject
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("department_id")]
    public Guid DepartmentId { get; set; }

    [Column("code")]
    public string Code { get; set; } = null!;

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("teacher_id")]
    public Guid? TeacherId { get; set; }

    [InverseProperty("Subject")]
    public virtual ICollection<Assignment> Assignments { get; set; } = new List<Assignment>();

    [ForeignKey("DepartmentId")]
    [InverseProperty("Subjects")]
    public virtual Department Department { get; set; } = null!;

    [InverseProperty("Subject")]
    public virtual ICollection<ExamSchedule> ExamSchedules { get; set; } = new List<ExamSchedule>();

    [InverseProperty("Subject")]
    public virtual ICollection<ExternalMark> ExternalMarks { get; set; } = new List<ExternalMark>();

    [InverseProperty("Subject")]
    public virtual ICollection<InternalMark> InternalMarks { get; set; } = new List<InternalMark>();

    [InverseProperty("Subject")]
    public virtual ICollection<Material> Materials { get; set; } = new List<Material>();

    [InverseProperty("Subject")]
    public virtual ICollection<ClassroomDiscussion> ClassroomDiscussions { get; set; } = new List<ClassroomDiscussion>();

    [InverseProperty("Subject")]
    public virtual ICollection<RegulationSubjectOffering> RegulationSubjectOfferings { get; set; } = new List<RegulationSubjectOffering>();

    [InverseProperty("Subject")]
    public virtual ICollection<SubjectTeacher> SubjectTeachers { get; set; } = new List<SubjectTeacher>();

    [ForeignKey("TeacherId")]
    [InverseProperty("Subjects")]
    public virtual User? Teacher { get; set; }

    [InverseProperty("Subject")]
    public virtual ICollection<TeacherSectionAssignment> TeacherSectionAssignments { get; set; } = new List<TeacherSectionAssignment>();

    [InverseProperty("Subject")]
    public virtual ICollection<TimetableSlot> TimetableSlots { get; set; } = new List<TimetableSlot>();
}
