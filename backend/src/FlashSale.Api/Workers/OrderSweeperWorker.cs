using FlashSale.Domain.Enums;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Workers;

/// <summary>
/// Expires abandoned baskets.
/// <para>
/// A buyer who holds seats to buy later but never pays would otherwise hold them
/// indefinitely, starving other buyers. This worker expires those orders and returns
/// their seats to the pool, using the index on (Status, ExpiresAtUtc).
/// </para>
/// </summary>
public sealed class OrderSweeperWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderSweeperWorker> _logger;

    public OrderSweeperWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<OrderSweeperWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Order sweeper starting.");

        using var timer = new PeriodicTimer(SweepInterval);

        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Order sweep failed; the next tick will retry.");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Bounded batch so the Orders table is never locked for long.
        var expired = await dbContext.Orders
            .Include(o => o.Seats)
            .Where(o => o.Status == OrderStatus.Pending.ToString() && o.ExpiresAtUtc <= now)
            .Take(BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expired.Count == 0)
        {
            return;
        }

        var ticketIds = expired.SelectMany(o => o.Seats).Select(s => s.TicketId).Distinct().ToList();

        var tickets = await dbContext.Tickets
            .Where(t => ticketIds.Contains(t.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var ticket in tickets)
        {
            // Never reclaim a seat that has already been paid for.
            if (ticket.Status == TicketStatus.Sold.ToString())
            {
                continue;
            }

            ticket.Status = TicketStatus.Available.ToString();
            ticket.AssignedUserId = null;
            ticket.ReservedUntilUtc = null;
        }

        foreach (var order in expired)
        {
            order.Status = OrderStatus.Expired.ToString();
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Expired {OrderCount} abandoned order(s), reclaiming {SeatCount} seat(s).",
            expired.Count, tickets.Count);
    }
}