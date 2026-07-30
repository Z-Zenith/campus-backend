using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("timetable_change_requests")]
public partial class TimetableChangeRequest
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("teacher_id")]
    public Guid TeacherId { get; set; }

    [Column("description")]
    public string Description { get; set; } = null!;

    [Column("status")]
    public TimetableChangeRequestStatus Status { get; set; }

    [Column("requested_at")]
    public DateTime RequestedAt { get; set; }

    [Column("reviewed_by")]
    public Guid? ReviewedBy { get; set; }

    [ForeignKey("ReviewedBy")]
    [InverseProperty("TimetableChangeRequestReviewedByNavigations")]
    public virtual User? ReviewedByNavigation { get; set; }

    [ForeignKey("TeacherId")]
    [InverseProperty("TimetableChangeRequestTeachers")]
    public virtual User Teacher { get; set; } = null!;
}
