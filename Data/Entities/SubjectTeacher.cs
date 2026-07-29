using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("subject_teachers")]
[Index("SubjectId", "TeacherId", Name = "subject_teachers_subject_id_teacher_id_key", IsUnique = true)]
public partial class SubjectTeacher
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    [Column("teacher_id")]
    public Guid TeacherId { get; set; }

    [ForeignKey("SubjectId")]
    [InverseProperty("SubjectTeachers")]
    public virtual Subject Subject { get; set; } = null!;

    [ForeignKey("TeacherId")]
    [InverseProperty("SubjectTeachers")]
    public virtual User Teacher { get; set; } = null!;
}
