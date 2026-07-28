using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("autograde_suggestions")]
public partial class AutogradeSuggestion
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("submission_id")]
    public Guid SubmissionId { get; set; }

    [Column("suggested_grade")]
    public decimal SuggestedGrade { get; set; }

    [Column("confidence")]
    public decimal? Confidence { get; set; }

    [Column("matched_criteria", TypeName = "nvarchar(max)")]
    public string? MatchedCriteria { get; set; }

    [Column("feedback", TypeName = "nvarchar(max)")]
    public string? Feedback { get; set; }

    [Column("confirmed_by_teacher")]
    public bool ConfirmedByTeacher { get; set; }

    [Column("confirmed_at")]
    public DateTime? ConfirmedAt { get; set; }

    [ForeignKey("SubmissionId")]
    [InverseProperty("AutogradeSuggestions")]
    public virtual Submission Submission { get; set; } = null!;
}
