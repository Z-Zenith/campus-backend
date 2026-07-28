using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("notifications")]
[Index("RecipientId", "CreatedAt", Name = "idx_notifications_recipient", IsDescending = new[] { false, true })]
public partial class Notification
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("recipient_id")]
    public Guid RecipientId { get; set; }

    [Column("payload", TypeName = "nvarchar(max)")]
    public string Payload { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("read_at")]
    public DateTime? ReadAt { get; set; }

    [ForeignKey("RecipientId")]
    [InverseProperty("Notifications")]
    public virtual User Recipient { get; set; } = null!;
}
