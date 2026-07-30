using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

// SDA-03 classification policy engine (SDA/SEK plan, Work Item A2): the always-block
// counterpart to WhitelistSite — same college-scoped shape, checked as a fast override
// before the classifier cache is ever consulted.
[Table("blocked_sites")]
[Index("CollegeId", "Url", Name = "blocked_sites_college_id_url_key", IsUnique = true)]
public partial class BlockedSite
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("college_id")]
    public Guid CollegeId { get; set; }

    [Column("url")]
    public string Url { get; set; } = null!;

    [Column("blocked_at")]
    public DateTime BlockedAt { get; set; }

    [Column("blocked_by")]
    public Guid BlockedBy { get; set; }

    [ForeignKey("CollegeId")]
    [InverseProperty("BlockedSites")]
    public virtual College College { get; set; } = null!;

    [ForeignKey("BlockedBy")]
    [InverseProperty("BlockedSiteBlockedByNavigations")]
    public virtual User BlockedByNavigation { get; set; } = null!;
}
