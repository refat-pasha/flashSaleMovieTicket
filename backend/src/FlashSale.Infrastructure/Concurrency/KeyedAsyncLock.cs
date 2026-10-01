using System.Collections.Concurrent;

namespace FlashSale.Infrastructure.Concurrency;

/// <summary>
/// Distributes independent <see cref="SemaphoreSlim"/> instances by string key.
/// <para>
/// This is a <em>complementary</em> optimisation to optimistic concurrency, not a
/// replacement for it. It collapses thousands of in-flight requests that target the
/// same seat into one database round-trip, dramatically reducing
/// <c>DbUpdateConcurrencyException</c> churn under a flash sale. Correctness still
/// comes from the <c>rowversion</c> token, which also protects against writers in
/// other processes or other API instances where this lock has no visibility.
/// </para>
/// Registered as a singleton.
/// </summary>
public sealed class KeyedAsyncLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="action"/> while holding the exclusive lock for
    /// <paramref name="key"/>. Always releases in a <c>finally</c>.
    /// </summary>
    public async Task<T> RunAsync<T>(string key, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Non-generic overload for void operations.</summary>
    public async Task RunAsync(string key, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        await RunAsync(key, async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the canonical lock key for a seat claim attempt.</summary>
    public static string ForTicket(Guid ticketId) => $"ticket:{ticketId}";
}