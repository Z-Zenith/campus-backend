using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Data.Entities;

[Table("payment_transactions")]
[Index("GatewayTxnId", Name = "payment_transactions_gateway_txn_id_key", IsUnique = true)]
public partial class PaymentTransaction
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("fee_record_id")]
    public Guid FeeRecordId { get; set; }

    [Column("gateway_txn_id")]
    public string GatewayTxnId { get; set; } = null!;

    [Column("status")]
    public PaymentTransactionStatus Status { get; set; }

    [Column("processed_at")]
    public DateTime ProcessedAt { get; set; }

    [ForeignKey("FeeRecordId")]
    [InverseProperty("PaymentTransactions")]
    public virtual FeeRecord FeeRecord { get; set; } = null!;
}
