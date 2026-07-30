using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("staff_group_members")]
[Index("StaffGroupId", "UserId", Name = "staff_group_members_staff_group_id_user_id_key", IsUnique = true)]
public partial class StaffGroupMember
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("staff_group_id")]
    public Guid StaffGroupId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("joined_at")]
    public DateTime JoinedAt { get; set; }

    [ForeignKey("StaffGroupId")]
    [InverseProperty("StaffGroupMembers")]
    public virtual StaffGroup StaffGroup { get; set; } = null!;

    [ForeignKey("UserId")]
    [InverseProperty("StaffGroupMemberships")]
    public virtual User User { get; set; } = null!;
}
