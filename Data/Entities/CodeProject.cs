using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("code_projects")]
[Index("OwnerId", "UpdatedAt", Name = "idx_code_projects_owner_updated", IsDescending = new[] { false, true })]
public partial class CodeProject
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("owner_id")]
    public Guid OwnerId { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("entry_file_path")]
    public string EntryFilePath { get; set; } = null!;

    [Column("active_file_path")]
    public string ActiveFilePath { get; set; } = null!;

    [Column("stdin")]
    public string Stdin { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [InverseProperty("Project")]
    public virtual ICollection<CodeFile> CodeFiles { get; set; } = new List<CodeFile>();

    [ForeignKey("OwnerId")]
    [InverseProperty("CodeProjects")]
    public virtual User Owner { get; set; } = null!;
}
