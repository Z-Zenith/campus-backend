using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("materials")]
public partial class Material
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("subject_id")]
    public Guid? SubjectId { get; set; }

    [Column("club_id")]
    public Guid? ClubId { get; set; }

    [Column("classroom_discussion_id")]
    public Guid? ClassroomDiscussionId { get; set; }

    [Column("staff_group_id")]
    public Guid? StaffGroupId { get; set; }

    [Column("uploaded_by")]
    public Guid UploadedBy { get; set; }

    [Column("file_url")]
    public string FileUrl { get; set; } = null!;

    [Column("title")]
    public string Title { get; set; } = null!;

    [Column("uploaded_at")]
    public DateTime UploadedAt { get; set; }

    [ForeignKey("ClubId")]
    [InverseProperty("Materials")]
    public virtual Club? Club { get; set; }

    [ForeignKey("ClassroomDiscussionId")]
    [InverseProperty("Materials")]
    public virtual ClassroomDiscussion? ClassroomDiscussion { get; set; }

    [ForeignKey("StaffGroupId")]
    [InverseProperty("Materials")]
    public virtual StaffGroup? StaffGroup { get; set; }

    [ForeignKey("SubjectId")]
    [InverseProperty("Materials")]
    public virtual Subject? Subject { get; set; }

    [ForeignKey("UploadedBy")]
    [InverseProperty("Materials")]
    public virtual User UploadedByNavigation { get; set; } = null!;
}
