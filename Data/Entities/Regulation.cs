using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("regulations")]
[Index("DepartmentId", "Code", Name = "regulations_department_id_code_key", IsUnique = true)]
public partial class Regulation
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("department_id")]
    public Guid DepartmentId { get; set; }

    [Column("code")]
    public string Code { get; set; } = null!;

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("effective_from_year")]
    public int EffectiveFromYear { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    [ForeignKey("DepartmentId")]
    [InverseProperty("Regulations")]
    public virtual Department Department { get; set; } = null!;

    [InverseProperty("Regulation")]
    public virtual ICollection<RegulationSubjectOffering> RegulationSubjectOfferings { get; set; } = new List<RegulationSubjectOffering>();
}
