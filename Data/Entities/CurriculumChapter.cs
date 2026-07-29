using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("curriculum_chapters")]
[Index("UnitId", "ChapterNumber", Name = "curriculum_chapters_unit_id_chapter_number_key", IsUnique = true)]
public partial class CurriculumChapter
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("unit_id")]
    public Guid UnitId { get; set; }

    [Column("chapter_number")]
    public int ChapterNumber { get; set; }

    [Column("title")]
    public string Title { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

    [ForeignKey("UnitId")]
    [InverseProperty("CurriculumChapters")]
    public virtual CurriculumUnit Unit { get; set; } = null!;
}
