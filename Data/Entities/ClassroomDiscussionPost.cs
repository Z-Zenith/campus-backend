using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("classroom_discussion_posts")]
[Index("ClassroomDiscussionId", "CreatedAt", Name = "idx_classroom_discussion_posts_discussion", IsDescending = new[] { false, true })]
public partial class ClassroomDiscussionPost
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("classroom_discussion_id")]
    public Guid ClassroomDiscussionId { get; set; }

    [Column("author_id")]
    public Guid AuthorId { get; set; }

    [Column("content")]
    public string Content { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("AuthorId")]
    [InverseProperty("ClassroomDiscussionPosts")]
    public virtual User Author { get; set; } = null!;

    [ForeignKey("ClassroomDiscussionId")]
    [InverseProperty("ClassroomDiscussionPosts")]
    public virtual ClassroomDiscussion ClassroomDiscussion { get; set; } = null!;
}
