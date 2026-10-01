using FlashSale.Domain.Entities;
using FlashSale.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlashSale.Infrastructure.Persistence;

public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("Events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Description)
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(e => e.Venue)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.StartsAtUtc);

        builder.Property(e => e.PriceInMinorUnits);

        builder.Property(e => e.CurrencyCode)
            .IsRequired()
            .HasMaxLength(3)
            .IsFixedLength();

        /// <summary>Guards against a misconfigured batch size stalling the whole sale.</summary>
        builder.Property(e => e.AdmitBatchSize)
            .HasDefaultValue(50);

        builder.Property(e => e.CheckoutWindowSeconds)
            .HasDefaultValue(120);

        builder.Property(e => e.CreatedAtUtc);
    }
}

/// <summary>Mapping for a basket of seats plus its payment.</summary>
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.UserId).IsRequired();
        builder.Property(o => o.EventId).IsRequired();

        builder.Property(o => o.Status)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>()
            .HasDefaultValue(OrderStatus.Pending.ToString());

        builder.Property(o => o.PaymentMethod)
            .HasMaxLength(16)
            .HasConversion<string>();

        builder.Property(o => o.TotalMinorUnits);
        builder.Property(o => o.CurrencyCode).IsRequired().HasMaxLength(3).IsFixedLength();

        builder.Property(o => o.CreatedAtUtc);
        builder.Property(o => o.ExpiresAtUtc);
        builder.Property(o => o.PaidAtUtc);

        builder.Property(o => o.RowVersion).IsRowVersion();

        /// <summary>Supports "does this buyer have a live basket for this sale?".</summary>
        builder.HasIndex(o => new { o.UserId, o.EventId, o.Status })
            .HasDatabaseName("IX_Orders_UserId_EventId_Status");

        /// <summary>The sweeper finds lapsed orders through this index.</summary>
        builder.HasIndex(o => new { o.Status, o.ExpiresAtUtc })
            .HasDatabaseName("IX_Orders_Status_ExpiresAtUtc");

        builder.HasOne(o => o.User)
            .WithMany(u => u.Orders)
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(o => o.Event)
            .WithMany(e => e.Orders)
            .HasForeignKey(o => o.EventId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Mapping for one seat line inside an order.</summary>
public sealed class OrderSeatConfiguration : IEntityTypeConfiguration<OrderSeat>
{
    public void Configure(EntityTypeBuilder<OrderSeat> builder)
    {
        builder.ToTable("OrderSeats");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.OrderId).IsRequired();
        builder.Property(s => s.TicketId).IsRequired();

        builder.Property(s => s.SeatNumber).IsRequired().HasMaxLength(32);
        builder.Property(s => s.UnitPriceMinorUnits);

        /// <summary>One line per seat, so an order can never list a seat twice.</summary>
        builder.HasIndex(s => s.TicketId)
            .IsUnique()
            .HasDatabaseName("IX_OrderSeats_TicketId");

        builder.HasOne(s => s.Order)
            .WithMany(o => o.Seats)
            .HasForeignKey(s => s.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}