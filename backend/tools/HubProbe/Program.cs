using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;

// Verifies the QueueHub end-to-end over a real WebSocket, through whichever base
// URL is supplied. This is the ground truth for the hub contract: authenticate,
// JoinQueue, then wait for the background worker to push QueueStatusChanged.
var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5099";

Console.WriteLine($"Probing {baseUrl}/hubs/queue");

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

// 1. Authenticate.
var login = await http.PostAsJsonAsync("/api/auth/login", new { email = $"probe-{Guid.NewGuid()}@test.local", displayName = "Hub Probe" });
login.EnsureSuccessStatusCode();

var auth = await login.Content.ReadFromJsonAsync<AuthResponse>();
Console.WriteLine($"  logged in: userId={auth!.UserId} checkoutAllowed={auth.CheckoutAllowed}");

// 2. Pick a seeded event.
var events = await http.GetFromJsonAsync<List<EventDto>>("/api/events");
var evt = events!.First(e => e.SeatsRemaining > 0);
Console.WriteLine($"  event: {evt.Name} ({evt.Id})");

// 3. Connect over WebSocket with the bearer token.
var hubUrl = $"{baseUrl}/hubs/queue";
var admitted = new TaskCompletionSource<QueueStatusChanged>(TaskCreationOptions.RunContinuationsAsynchronously);

var connection = new HubConnectionBuilder()
    .WithUrl(hubUrl, options => options.AccessTokenProvider = () => Task.FromResult<string?>(auth.AccessToken))
    .WithAutomaticReconnect()
    .Build();

connection.On<QueueStatusChanged>("QueueStatusChanged", status =>
{
    Console.WriteLine($"  <- QueueStatusChanged: status={status.Status} position={status.Position?.ToString() ?? "null"} totalWaiting={status.TotalWaiting} pass={(status.CheckoutPassToken is null ? "none" : "yes")}");

    if (status.Status == "Admitted" && status.CheckoutPassToken is not null)
    {
        admitted.TrySetResult(status);
    }
});

connection.On<QueueStatusChanged>("QueueJoined", status =>
    Console.WriteLine($"  <- QueueJoined: position={status.Position?.ToString() ?? "null"}"));

await connection.StartAsync();
Console.WriteLine($"  connected: {connection.State}");

// 4. Join the queue.
await connection.InvokeAsync("JoinQueue", new JoinQueueRequest { EventId = evt.Id });
Console.WriteLine("  JoinQueue invoked");

// 5. Wait for the worker to admit this buyer (10s tick).
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));

try
{
    var result = await admitted.Task.WaitAsync(timeout.Token);
    Console.WriteLine("  RESULT: ADMITTED with a checkout pass token");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine($"  RESULT: TIMED OUT still waiting (state={connection.State})");
    return 1;
}
finally
{
    await connection.DisposeAsync();
}

internal sealed record AuthResponse(
    string AccessToken,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("checkoutAllowed")] bool CheckoutAllowed);

internal sealed record EventDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("seatsRemaining")] int SeatsRemaining);

internal sealed record QueueStatusChanged(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("position")] int? Position,
    [property: JsonPropertyName("totalWaiting")] int TotalWaiting,
    [property: JsonPropertyName("checkoutPassToken")] string? CheckoutPassToken,
    [property: JsonPropertyName("passExpiresAtUtc")] DateTimeOffset? PassExpiresAtUtc,
    [property: JsonPropertyName("serverUtc")] DateTimeOffset ServerUtc);

internal sealed class JoinQueueRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("eventId")]
    public Guid EventId { get; set; }
}