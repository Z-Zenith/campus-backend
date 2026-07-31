using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("staff_group_posts")]
[Index("StaffGroupId", "CreatedAt", Name = "idx_staff_group_posts_group", IsDescending = new[] { false, true })]
public partial class StaffGroupPost
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("staff_group_id")]
    public Guid StaffGroupId { get; set; }

    [Column("author_id")]
    public Guid AuthorId { get; set; }

    [Column("content")]
    public string Content { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("AuthorId")]
    [InverseProperty("StaffGroupPosts")]
    public virtual User Author { get; set; } = null!;

    [ForeignKey("StaffGroupId")]
    [InverseProperty("StaffGroupPosts")]
    public virtual StaffGroup StaffGroup { get; set; } = null!;
}
