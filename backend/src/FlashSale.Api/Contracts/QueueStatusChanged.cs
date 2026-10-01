namespace FlashSale.Api.Contracts;

/// <summary>
/// Payload pushed by <c>QueueBackgroundWorker</c> over SignalR on the
/// <c>"QueueStatusChanged"</c> client method — the single event the Angular waiting
/// room reacts to. Serialised with camelCase by the default SignalR JSON options so
/// the TypeScript interface matches field-for-field.
/// </summary>
public sealed record QueueStatusChanged(
    /// <summary>Sale the update concerns.</summary>
    Guid EventId,

    /// <summary>"Waiting" or "Admitted".</summary>
    string Status,

    /// <summary>1-based place in line. Null once admitted.</summary>
    int? Position,

    /// <summary>Total buyers still waiting.</summary>
    int TotalWaiting,

    /// <summary>
    /// Temporary checkout pass. Populated only when <paramref name="Status"/> is
    /// "Admitted"; this is the JWT the client presents to the booking endpoint.
    /// </summary>
    string? CheckoutPassToken,

    /// <summary>When the pass stops being honoured.</summary>
    DateTimeOffset? PassExpiresAtUtc,

    /// <summary>Server clock, so the client can correct for drift when counting down.</summary>
    DateTimeOffset ServerUtc);