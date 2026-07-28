using System.ComponentModel.DataAnnotations.Schema;

namespace BackendApi.Data.Entities;

[Table("event_restricted_departments")]
public partial class EventRestrictedDepartment
{
    [Column("event_id")]
    public Guid EventId { get; set; }

    [Column("department_id")]
    public Guid DepartmentId { get; set; }

    [ForeignKey("EventId")]
    [InverseProperty("RestrictedDepartments")]
    public virtual Event Event { get; set; } = null!;

    [ForeignKey("DepartmentId")]
    public virtual Department Department { get; set; } = null!;
}
