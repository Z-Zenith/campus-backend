using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("curriculum_units")]
[Index("OfferingId", "UnitNumber", Name = "curriculum_units_offering_id_unit_number_key", IsUnique = true)]
public partial class CurriculumUnit
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("offering_id")]
    public Guid OfferingId { get; set; }

    [Column("unit_number")]
    public int UnitNumber { get; set; }

    [Column("title")]
    public string Title { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

    [ForeignKey("OfferingId")]
    [InverseProperty("CurriculumUnits")]
    public virtual RegulationSubjectOffering Offering { get; set; } = null!;

    [InverseProperty("Unit")]
    public virtual ICollection<CurriculumChapter> CurriculumChapters { get; set; } = new List<CurriculumChapter>();
}
