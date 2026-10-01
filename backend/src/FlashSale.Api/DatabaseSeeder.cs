using FlashSale.Domain.Entities;
using FlashSale.Domain.Enums;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api;

/// <summary>
/// Populates ten demo movies with seat inventory so the waiting room has something to
/// queue for. Idempotent: it returns immediately if inventory already exists, so it is
/// safe to leave enabled in Development.
/// </summary>
public static class DatabaseSeeder
{
    /// <summary>
    /// Ten films across genres and price bands, so the catalogue looks real and the
    /// seat maps differ in size.
    /// </summary>
    private static readonly (string Name, string Venue, string Blurb, long Price, int AdmitBatch, int Rows, int SeatsPerRow)[] Movies =
    [
        ("The Silent Harbour",      "Riverside Cinema 1", "A lighthouse keeper hears a wreck that never happened.",        1250, 50, 5, 20),
        ("Neon Requiem",            "Riverside Cinema 3", "A getaway driver trades her memories for one last escape.",  1650, 40, 6, 20),
        ("Paper Lanterns",         "Aurora Arts Centre", "Two strangers share letters through a wall for thirty years.",  900, 60, 4, 16),
        ("Gravity Bloom",          "Aurora IMAX",        "Astronauts lose the station and find each other.",             2200, 30, 6, 24),
        ("The Copper Thief",       "Riverside Cinema 2", "A locksmith with a conscience takes the one job he regrets.",  1100, 50, 5, 20),
        ("Midnight at Dahlia House","Aurora Arts Centre", "A solstice party where nobody remembers leaving.",              1400, 45, 5, 20),
        ("Iron Harvest",           "Riverside Cinema 5", "Farmhands fight something under the ploughed fields.",       1350, 50, 5, 20),
        ("The Last Analogue",      "Aurora IMAX",        "A projectionist realises the film he is splicing is a memory.", 1800, 35, 6, 24),
        ("Salt and Static",        "Riverside Cinema 4", "Two rival radio hosts, one transmitter, one long night.",       1050, 55, 5, 20),
        ("Winterlight",            "Aurora Arts Centre", "A translator falls for a voice on an old recording.",           1200, 50, 5, 20)
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seeder");

        if (await context.Events.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("Seed skipped: inventory already present.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var events = new List<Event>(Movies.Length);
        var tickets = new List<Ticket>();
        var totalSeats = 0;
        var totalValue = 0L;

        for (var i = 0; i < Movies.Length; i++)
        {
            var (name, venue, blurb, price, admitBatch, rows, seatsPerRow) = Movies[i];

            var evt = new Event
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = blurb,
                Venue = venue,
                // Staggered start times so the catalogue has a natural ordering.
                StartsAtUtc = now.AddDays(7 + (i * 3)),
                PriceInMinorUnits = price,
                CurrencyCode = "GBP",
                AdmitBatchSize = admitBatch,
                CheckoutWindowSeconds = 120,
                CreatedAtUtc = now
            };

            events.Add(evt);
            totalSeats += rows * seatsPerRow;

            for (var row = 1; row <= rows; row++)
            {
                for (var seat = 1; seat <= seatsPerRow; seat++)
                {
                    tickets.Add(new Ticket
                    {
                        Id = Guid.NewGuid(),
                        EventId = evt.Id,
                        SeatNumber = $"{row}-{seat:D2}",
                        Status = TicketStatus.Available.ToString(),
                        AssignedUserId = null,
                        ReservedUntilUtc = null,
                        PriceInMinorUnits = price
                    });

                    totalValue += price;
                }
            }
        }

        context.Events.AddRange(events);
        context.Tickets.AddRange(tickets);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Seeded {EventCount} movies and {TicketCount} seats (total inventory value {Value} minor units).",
            events.Count, tickets.Count, totalValue);
    }
}