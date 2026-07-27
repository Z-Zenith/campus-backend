using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("code_files")]
[Index("ProjectId", "Path", Name = "code_files_project_id_path_key", IsUnique = true)]
[Index("ProjectId", Name = "idx_code_files_project")]
public partial class CodeFile
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("project_id")]
    public Guid ProjectId { get; set; }

    [Column("path")]
    public string Path { get; set; } = null!;

    [Column("language")]
    public string Language { get; set; } = null!;

    [Column("content")]
    public string Content { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [ForeignKey("ProjectId")]
    [InverseProperty("CodeFiles")]
    public virtual CodeProject Project { get; set; } = null!;
}
