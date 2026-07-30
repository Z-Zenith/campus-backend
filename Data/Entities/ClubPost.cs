using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("club_posts")]
[Index("ClubId", "CreatedAt", Name = "idx_club_posts_club", IsDescending = new[] { false, true })]
public partial class ClubPost
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("club_id")]
    public Guid ClubId { get; set; }

    [Column("author_id")]
    public Guid AuthorId { get; set; }

    [Column("content")]
    public string Content { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("AuthorId")]
    [InverseProperty("ClubPosts")]
    public virtual User Author { get; set; } = null!;

    [ForeignKey("ClubId")]
    [InverseProperty("ClubPosts")]
    public virtual Club Club { get; set; } = null!;
}
