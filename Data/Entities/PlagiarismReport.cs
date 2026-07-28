using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("plagiarism_reports")]
public partial class PlagiarismReport
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("submission_id")]
    public Guid SubmissionId { get; set; }

    [Column("similarity_score")]
    public decimal SimilarityScore { get; set; }

    [Column("copyleaks_scan_id")]
    public string? CopyleaksScanId { get; set; }

    [Column("matched_sources", TypeName = "nvarchar(max)")]
    public string? MatchedSources { get; set; }

    [Column("checked_at")]
    public DateTime CheckedAt { get; set; }

    [ForeignKey("SubmissionId")]
    [InverseProperty("PlagiarismReports")]
    public virtual Submission Submission { get; set; } = null!;
}
