using FlashSale.Domain.Entities;
using FlashSale.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// Mapping for the contended <see cref="Ticket"/> aggregate.
/// This is the single most important configuration in the system: it defines
/// how the database detects two buyers racing for the same seat.
/// </summary>
public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .ValueGeneratedNever();

        builder.Property(t => t.EventId)
            .IsRequired();

        builder.Property(t => t.SeatNumber)
            .IsRequired()
            .HasMaxLength(32);

        /// <summary>
        /// Stored as a string ("Available" / "Reserved" / "Sold") rather than an int,
        /// so the table is readable and safe to reorder later.
        /// </summary>
        builder.Property(t => t.Status)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>()
            .HasDefaultValue(TicketStatus.Available.ToString());

        builder.Property(t => t.AssignedUserId);

        builder.Property(t => t.ReservedUntilUtc);

        builder.Property(t => t.PriceInMinorUnits);

        /// <summary>
        /// Optimistic concurrency token.
        /// <c>IsRowVersion()</c> maps to a SQL Server <c>rowversion</c> column, which the
        /// database increments on every UPDATE. EF includes the value it read in the
        /// WHERE clause; if another transaction already moved the row on, zero rows are
        /// affected and EF raises <c>DbUpdateConcurrencyException</c> — the exact signal
        /// the booking endpoint converts into an HTTP 409.
        /// Also marks the property as store-generated, so it must never be assigned in C#.
        /// </summary>
        builder.Property(t => t.RowVersion)
            .IsRowVersion();

        builder.HasIndex(t => t.EventId)
            .HasDatabaseName("IX_Tickets_EventId");

        /// <summary>
        /// Seat numbers are unique per event. This index is also what turns a
        /// duplicate-insert bug into an immediate constraint violation.
        /// </summary>
        builder.HasIndex(t => new { t.EventId, t.SeatNumber })
            .IsUnique()
            .HasDatabaseName("IX_Tickets_EventId_SeatNumber");

        /// <summary>
        /// Supports the "which seats are still free for this event?" query without
        /// scanning every row, and keeps the reservation sweep off a table scan.
        /// </summary>
        builder.HasIndex(t => new { t.EventId, t.Status })
            .HasDatabaseName("IX_Tickets_EventId_Status");

        builder.HasOne(t => t.Event)
            .WithMany(e => e.Tickets)
            .HasForeignKey(t => t.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.AssignedUser)
            .WithMany(u => u.Tickets)
            .HasForeignKey(t => t.AssignedUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}