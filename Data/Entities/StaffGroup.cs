using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

// Teacher-only space (e.g. "Staff Room") - all that's left of the old flat `groups` table
// once Club/SubjectSection moved out to their own concepts. See db/init/01_schema.sql.
[Table("staff_groups")]
[Index("CollegeId", Name = "idx_staff_groups_college")]
public partial class StaffGroup
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("college_id")]
    public Guid CollegeId { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("CollegeId")]
    [InverseProperty("StaffGroups")]
    public virtual College College { get; set; } = null!;

    [ForeignKey("CreatedBy")]
    [InverseProperty("StaffGroupsCreated")]
    public virtual User? CreatedByNavigation { get; set; }

    [InverseProperty("StaffGroup")]
    public virtual ICollection<StaffGroupMember> StaffGroupMembers { get; set; } = new List<StaffGroupMember>();

    [InverseProperty("StaffGroup")]
    public virtual ICollection<StaffGroupPost> StaffGroupPosts { get; set; } = new List<StaffGroupPost>();

    [InverseProperty("StaffGroup")]
    public virtual ICollection<Material> Materials { get; set; } = new List<Material>();
}
