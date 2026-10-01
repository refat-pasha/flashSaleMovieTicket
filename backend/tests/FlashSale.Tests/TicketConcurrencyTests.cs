using FlashSale.Domain.Entities;
using FlashSale.Domain.Enums;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Tests;

/// <summary>
/// Proves the <c>rowversion</c> concurrency token actually protects the hot ticket row.
/// <para>
/// The controller additionally serialises attempts with <c>KeyedAsyncLock</c>, which
/// masks this exception inside a single API process. This test deliberately bypasses
/// that lock and uses two independent <see cref="ApplicationDbContext"/> instances —
/// the same situation as two API instances or any other writer — to show the
/// database itself is the real arbiter.
/// </para>
/// Uses the live SQLEXPRESS database and creates its own isolated ticket.
/// </summary>
public sealed class TicketConcurrencyTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=localhost\\SQLEXPRESS;Database=FlashSaleEngine;Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _ticketId;
    private Guid _eventId;
    private Guid _buyerOne;
    private Guid _buyerTwo;

    public async Task InitializeAsync()
    {
        await using var setup = CreateContext();

        _eventId = Guid.NewGuid();
        _ticketId = Guid.NewGuid();
        _buyerOne = Guid.NewGuid();
        _buyerTwo = Guid.NewGuid();

        setup.Events.Add(new Event
        {
            Id = _eventId,
            Name = "Concurrency harness",
            Description = "Test fixture",
            Venue = "Test",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            PriceInMinorUnits = 1000L,
            CurrencyCode = "GBP",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        // Tickets.AssignedUserId is FK-constrained to Users, so the buyers must exist
        // as real rows or the UPDATE fails on the constraint before concurrency matters.
        setup.Users.Add(new User
        {
            Id = _buyerOne,
            Email = "buyer-one@test.local",
            DisplayName = "Buyer One",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        setup.Users.Add(new User
        {
            Id = _buyerTwo,
            Email = "buyer-two@test.local",
            DisplayName = "Buyer Two",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        setup.Tickets.Add(new Ticket
        {
            Id = _ticketId,
            EventId = _eventId,
            SeatNumber = "TEST-1",
            Status = TicketStatus.Available.ToString(),
            PriceInMinorUnits = 1000L
        });

        await setup.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var teardown = CreateContext();
        await teardown.Database.ExecuteSqlRawAsync("DELETE FROM Tickets WHERE Id = {0}", _ticketId);
        await teardown.Database.ExecuteSqlRawAsync("DELETE FROM Events WHERE Id = {0}", _eventId);
        await teardown.Database.ExecuteSqlRawAsync(
            "DELETE FROM Users WHERE Id IN ({0}, {1})", _buyerOne, _buyerTwo);
    }

    private static ApplicationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);

    [Fact]
    public async Task SecondWriter_WithStaleRowVersion_ThrowsDbUpdateConcurrencyException()
    {
        // Both buyers read the same row, so both hold the identical RowVersion.
        await using var contextOne = CreateContext();
        await using var contextTwo = CreateContext();

        var ticketOne = await contextOne.Tickets.SingleAsync(t => t.Id == _ticketId);
        var ticketTwo = await contextTwo.Tickets.SingleAsync(t => t.Id == _ticketId);

        Assert.Equal(ticketOne.RowVersion, ticketTwo.RowVersion);

        // Buyer one wins the seat.
        ticketOne.Status = TicketStatus.Reserved.ToString();
        ticketOne.AssignedUserId = _buyerOne;
        await contextOne.SaveChangesAsync();

        // Buyer two writes against the now-stale version.
        ticketTwo.Status = TicketStatus.Reserved.ToString();
        ticketTwo.AssignedUserId = _buyerTwo;

        // This is exactly the exception BookingController.TryClaimAsync catches and
        // converts into HTTP 409.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => contextTwo.SaveChangesAsync());

        // Buyer one's claim must survive untouched.
        await using var verify = CreateContext();
        var persisted = await verify.Tickets.SingleAsync(t => t.Id == _ticketId);
        Assert.Equal(_buyerOne, persisted.AssignedUserId);
    }

    [Fact]
    public async Task RowVersion_IsGeneratedByDatabase_AndChangesOnUpdate()
    {
        await using var context = CreateContext();
        var ticket = await context.Tickets.SingleAsync(t => t.Id == _ticketId);

        // Defensive copy: EF updates the tracked entity's RowVersion after the UPDATE,
        // so holding the reference would alias the value under test.
        var original = ticket.RowVersion.ToArray();
        Assert.NotEmpty(original);

        ticket.Status = TicketStatus.Reserved.ToString();
        ticket.AssignedUserId = _buyerOne;
        await context.SaveChangesAsync();

        await context.Entry(ticket).ReloadAsync();

        Assert.False(original.SequenceEqual(ticket.RowVersion),
            "rowversion must change on every update, otherwise the concurrency token is useless.");
    }
}