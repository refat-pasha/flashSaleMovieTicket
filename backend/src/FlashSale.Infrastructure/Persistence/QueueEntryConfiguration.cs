using FlashSale.Domain.Entities;
using FlashSale.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlashSale.Infrastructure.Persistence;

public sealed class QueueEntryConfiguration : IEntityTypeConfiguration<QueueEntry>
{
    public void Configure(EntityTypeBuilder<QueueEntry> builder)
    {
        builder.ToTable("QueueEntries");
        builder.HasKey(q => q.Id);

        builder.Property(q => q.Id).ValueGeneratedNever();

        builder.Property(q => q.EventId).IsRequired();
        builder.Property(q => q.UserId).IsRequired();

        builder.Property(q => q.Status)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>()
            .HasDefaultValue(QueueStatus.Waiting.ToString());

        builder.Property(q => q.SequenceNumber);
        builder.Property(q => q.EnqueuedAtUtc);
        builder.Property(q => q.AdmittedAtUtc);
        builder.Property(q => q.CheckoutPassExpiresAtUtc);

        /// <summary>
        /// The queue is always read as "next up for this event, oldest first".
        /// </summary>
        builder.HasIndex(q => new { q.EventId, q.SequenceNumber })
            .HasDatabaseName("IX_QueueEntries_EventId_SequenceNumber");

        /// <summary>Prevents one buyer occupying two places in the same sale queue.</summary>
        builder.HasIndex(q => new { q.EventId, q.UserId })
            .IsUnique()
            .HasDatabaseName("IX_QueueEntries_EventId_UserId");

        builder.HasOne(q => q.Event)
            .WithMany(e => e.QueueEntries)
            .HasForeignKey(q => q.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(q => q.User)
            .WithMany(u => u.QueueEntries)
            .HasForeignKey(q => q.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}