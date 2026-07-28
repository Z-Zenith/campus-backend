using System.ComponentModel.DataAnnotations.Schema;

namespace BackendApi.Data.Entities;

[Table("event_restricted_years")]
public partial class EventRestrictedYear
{
    [Column("event_id")]
    public Guid EventId { get; set; }

    [Column("year")]
    public int Year { get; set; }

    [ForeignKey("EventId")]
    [InverseProperty("RestrictedYears")]
    public virtual Event Event { get; set; } = null!;
}
