using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

// SDA-03 classification policy engine (SDA/SEK plan, Work Item A2): a student's "this is
// wrongly blocked/allowed" report. Individual rows are kept for audit/accountability, but
// the hybrid score only ever reads the college-wide aggregate across this table for a
// given host — see Services/SiteReputationAggregator.cs.
[Table("site_classification_feedback")]
[Index("CollegeId", "Host", Name = "idx_site_classification_feedback_college_host")]
public partial class SiteClassificationFeedback
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("college_id")]
    public Guid CollegeId { get; set; }

    [Column("host")]
    public string Host { get; set; } = null!;

    [Column("student_id")]
    public Guid StudentId { get; set; }

    [Column("feedback")]
    public SiteClassificationFeedbackType Feedback { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("CollegeId")]
    [InverseProperty("SiteClassificationFeedbacks")]
    public virtual College College { get; set; } = null!;

    [ForeignKey("StudentId")]
    [InverseProperty("SiteClassificationFeedbacks")]
    public virtual User Student { get; set; } = null!;
}
