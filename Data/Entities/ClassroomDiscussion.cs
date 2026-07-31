using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

// One per (Section, Subject) - e.g. "3rd Year CSE-A - Data Structures". Auto-provisioned
// from TeacherSectionAssignment; no stored membership - see db/init/01_schema.sql's comment
// for why (derived from SectionEnrollment + TeacherSectionAssignment instead).
[Table("classroom_discussions")]
[Index("SectionId", "SubjectId", Name = "classroom_discussions_section_id_subject_id_key", IsUnique = true)]
public partial class ClassroomDiscussion
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("section_id")]
    public Guid SectionId { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("SectionId")]
    [InverseProperty("ClassroomDiscussions")]
    public virtual Section Section { get; set; } = null!;

    [ForeignKey("SubjectId")]
    [InverseProperty("ClassroomDiscussions")]
    public virtual Subject Subject { get; set; } = null!;

    [InverseProperty("ClassroomDiscussion")]
    public virtual ICollection<ClassroomDiscussionPost> ClassroomDiscussionPosts { get; set; } = new List<ClassroomDiscussionPost>();

    [InverseProperty("ClassroomDiscussion")]
    public virtual ICollection<Material> Materials { get; set; } = new List<Material>();
}
