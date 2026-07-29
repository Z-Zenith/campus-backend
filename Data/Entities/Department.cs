using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("departments")]
public partial class Department
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("college_id")]
    public Guid CollegeId { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("hod_role_binding_id")]
    public Guid? HodRoleBindingId { get; set; }

    [ForeignKey("CollegeId")]
    [InverseProperty("Departments")]
    public virtual College College { get; set; } = null!;

    [ForeignKey("HodRoleBindingId")]
    [InverseProperty("Departments")]
    public virtual RoleBinding? HodRoleBinding { get; set; }

    [InverseProperty("Department")]
    public virtual ICollection<Regulation> Regulations { get; set; } = new List<Regulation>();

    [InverseProperty("Department")]
    public virtual ICollection<RoleBinding> RoleBindings { get; set; } = new List<RoleBinding>();

    [InverseProperty("Department")]
    public virtual ICollection<Section> Sections { get; set; } = new List<Section>();

    [InverseProperty("Department")]
    public virtual ICollection<Subject> Subjects { get; set; } = new List<Subject>();

    [InverseProperty("Department")]
    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
