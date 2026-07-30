using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("role_bindings")]
[Index("SectionId", Name = "idx_role_bindings_section")]
public partial class RoleBinding
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("role_code")]
    public string RoleCode { get; set; } = null!;

    [Column("department_id")]
    public Guid? DepartmentId { get; set; }

    [Column("section_id")]
    public Guid? SectionId { get; set; }

    [Column("granted_at")]
    public DateTime GrantedAt { get; set; }

    [ForeignKey("DepartmentId")]
    [InverseProperty("RoleBindings")]
    public virtual Department? Department { get; set; }

    [ForeignKey("SectionId")]
    [InverseProperty("RoleBindings")]
    public virtual Section? Section { get; set; }

    [InverseProperty("HodRoleBinding")]
    public virtual ICollection<Department> Departments { get; set; } = new List<Department>();

    [ForeignKey("RoleCode")]
    [InverseProperty("RoleBindings")]
    public virtual Role RoleCodeNavigation { get; set; } = null!;

    [ForeignKey("UserId")]
    [InverseProperty("RoleBindings")]
    public virtual User User { get; set; } = null!;
}
