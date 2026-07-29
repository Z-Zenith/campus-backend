using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("todos")]
public partial class Todo
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("student_id")]
    public Guid StudentId { get; set; }

    [Column("title")]
    public string Title { get; set; } = null!;

    [Column("due_date")]
    public DateTime? DueDate { get; set; }

    [Column("completed")]
    public bool Completed { get; set; }

    [Column("priority")]
    public int Priority { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [ForeignKey("StudentId")]
    [InverseProperty("Todos")]
    public virtual User Student { get; set; } = null!;
}
