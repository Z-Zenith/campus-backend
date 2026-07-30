using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("club_members")]
[Index("ClubId", "UserId", Name = "club_members_club_id_user_id_key", IsUnique = true)]
public partial class ClubMember
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("club_id")]
    public Guid ClubId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("joined_at")]
    public DateTime JoinedAt { get; set; }

    [ForeignKey("ClubId")]
    [InverseProperty("ClubMembers")]
    public virtual Club Club { get; set; } = null!;

    [ForeignKey("UserId")]
    [InverseProperty("ClubMemberships")]
    public virtual User User { get; set; } = null!;
}
