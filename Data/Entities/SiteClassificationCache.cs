using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

// SDA-03 classification policy engine (SDA/SEK plan, Work Item A2): the real shared
// source of truth for "what did we decide about this domain" — keyed by (college_id,
// host), not by student or device, so every student at the same college hitting the
// same host reads the same row. See BrowsingController's POST /browser/classify.
[Table("site_classification_cache")]
[Index("CollegeId", "Host", Name = "site_classification_cache_college_id_host_key", IsUnique = true)]
public partial class SiteClassificationCache
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("college_id")]
    public Guid CollegeId { get; set; }

    [Column("host")]
    public string Host { get; set; } = null!;

    [Column("hybrid_score")]
    public double HybridScore { get; set; }

    [Column("allowed")]
    public bool Allowed { get; set; }

    [Column("matched_category")]
    public string? MatchedCategory { get; set; }

    [Column("computed_at")]
    public DateTime ComputedAt { get; set; }

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [ForeignKey("CollegeId")]
    [InverseProperty("SiteClassificationCaches")]
    public virtual College College { get; set; } = null!;
}
