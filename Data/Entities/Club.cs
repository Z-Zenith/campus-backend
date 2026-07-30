using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

// Opt-in student org. Led by a faculty lead (teacher) + a student incharge (officer),
// alongside regular members (ClubMembers) - see db/init/01_schema.sql's comment for why
// leadership is a direct FK pair rather than routed through RoleBinding.
[Table("clubs")]
[Index("CollegeId", Name = "idx_clubs_college")]
public partial class Club
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("college_id")]
    public Guid CollegeId { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

    [Column("faculty_lead_user_id")]
    public Guid? FacultyLeadUserId { get; set; }

    [Column("student_incharge_user_id")]
    public Guid? StudentInchargeUserId { get; set; }

    // Club-authored HTML/CSS/JS home site - see the schema comment: render only inside a
    // sandboxed iframe (no allow-same-origin, strict CSP), never trusted as safe markup.
    [Column("home_site_html")]
    public string? HomeSiteHtml { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [ForeignKey("CollegeId")]
    [InverseProperty("Clubs")]
    public virtual College College { get; set; } = null!;

    [ForeignKey("FacultyLeadUserId")]
    [InverseProperty("ClubsAsFacultyLead")]
    public virtual User? FacultyLead { get; set; }

    [ForeignKey("StudentInchargeUserId")]
    [InverseProperty("ClubsAsStudentIncharge")]
    public virtual User? StudentIncharge { get; set; }

    [ForeignKey("CreatedBy")]
    [InverseProperty("ClubsCreated")]
    public virtual User? CreatedByNavigation { get; set; }

    [InverseProperty("Club")]
    public virtual ICollection<ClubMember> ClubMembers { get; set; } = new List<ClubMember>();

    [InverseProperty("Club")]
    public virtual ICollection<ClubPost> ClubPosts { get; set; } = new List<ClubPost>();

    [InverseProperty("Club")]
    public virtual ICollection<Material> Materials { get; set; } = new List<Material>();
}
