using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("documents")]
public partial class Document
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("owner_id")]
    public Guid OwnerId { get; set; }

    [Column("file_url")]
    public string FileUrl { get; set; } = null!;

    [Column("annotations", TypeName = "nvarchar(max)")]
    public string? Annotations { get; set; }

    [Column("page_count")]
    public int? PageCount { get; set; }

    [ForeignKey("OwnerId")]
    [InverseProperty("Documents")]
    public virtual User Owner { get; set; } = null!;
}
