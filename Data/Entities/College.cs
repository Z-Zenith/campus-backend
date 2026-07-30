using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("colleges")]
public partial class College
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    // IANA time zone name (e.g. "Asia/Kolkata"). Used to derive session/due dates from this
    // college's local time rather than raw UTC (#152) - see Services/CollegeClock.cs.
    [Column("time_zone")]
    public string TimeZone { get; set; } = "UTC";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [InverseProperty("College")]
    public virtual ICollection<Department> Departments { get; set; } = new List<Department>();

    [InverseProperty("College")]
    public virtual ICollection<Event> Events { get; set; } = new List<Event>();

    [InverseProperty("College")]
    public virtual ICollection<Club> Clubs { get; set; } = new List<Club>();

    [InverseProperty("College")]
    public virtual ICollection<StaffGroup> StaffGroups { get; set; } = new List<StaffGroup>();

    [InverseProperty("College")]
    public virtual ICollection<User> Users { get; set; } = new List<User>();

    [InverseProperty("College")]
    public virtual ICollection<WhitelistSite> WhitelistSites { get; set; } = new List<WhitelistSite>();
}
