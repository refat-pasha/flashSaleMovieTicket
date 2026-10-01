using FlashSale.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// EF Core unit of work for the ticketing engine.
/// Fluent API is used throughout (no data annotations) so the concurrency
/// and indexing strategy for the hot <see cref="Ticket"/> table is explicit.
/// </summary>
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<QueueEntry> QueueEntries => Set<QueueEntry>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderSeat> OrderSeats => Set<OrderSeat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}