using System.Text;
using FlashSale.Api.Hubs;
using FlashSale.Api.Workers;
using FlashSale.Infrastructure.Concurrency;
using FlashSale.Infrastructure.Identity;
using FlashSale.Infrastructure.Persistence;
using FlashSale.Infrastructure.Queueing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
// Note: Microsoft.OpenApi 2.x moved OpenApiInfo and friends out of the old
// Microsoft.OpenApi.Models namespace and into Microsoft.OpenApi itself.
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
    {
        // Resilient execution: transient faults (failover, network blips) are retried
        // automatically instead of surfacing as 500s in the middle of a sale.
        sql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null);

        sql.CommandTimeout(30);
    }));

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
// Singleton: the queue is shared by the hub and the background worker inside this API
// instance. A multi-instance deployment would swap this for a Redis-backed store.
builder.Services.AddSingleton<IQueueStore, InMemoryQueueStore>();
builder.Services.AddSingleton<KeyedAsyncLock>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

// JwtProvider validates its key in the constructor, so a bad key fails at startup.
builder.Services.AddSingleton<JwtProvider>();

builder.Services.AddHostedService<QueueBackgroundWorker>();
builder.Services.AddHostedService<ReservationSweeperWorker>();
builder.Services.AddHostedService<OrderSweeperWorker>();

// ---------------------------------------------------------------------------
// SignalR
// ---------------------------------------------------------------------------
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();

    // A flash sale means many small frames; allow larger messages so queue snapshots are
    // not fragmented, and keep the transport warm between ticks.
    options.MaximumReceiveMessageSize = 64 * 1024;
    options.StreamBufferCapacity = 64 * 1024;
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
});

// ---------------------------------------------------------------------------
// CORS for the Angular dev server
// ---------------------------------------------------------------------------
const string SpaCorsPolicy = "SpaCors";
builder.Services.AddCors(options =>
{
    options.AddPolicy(SpaCorsPolicy, policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                      ?? ["http://localhost:4200"];

        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              // Required: SignalR sends credentials during the negotiate handshake.
              .AllowCredentials();
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Flash Sale Ticketing Engine",
        Version = "v1",
        Description = "High-throughput ticketing with a virtual waiting room."
    });

    // Swashbuckle 10 pairs a security scheme definition with a reference to it.
    // The reference carries the "Bearer" id, which is what ties the requirement below
    // to the definition registered by AddSecurityDefinition.
    var scheme = new OpenApiSecuritySchemeReference("Bearer");

    var definition = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT issued by /api/auth/login."
    };

    options.AddSecurityDefinition("Bearer", definition);

    // Document-wide requirement so the "Authorize" button works in the Swagger UI.
    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement { [scheme] = [] });
});
// ---------------------------------------------------------------------------
// Authentication / authorisation
// ---------------------------------------------------------------------------
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) || jwtOptions.SigningKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey must be configured with at least 32 characters. " +
        "Set it via user secrets, an environment variable, or a vault reference.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(jwtOptions.ClockSkewSeconds),

            // Short-lived tokens make a strict window worthwhile.
            RequireExpirationTime = true,
            RequireSignedTokens = true
        };

        // WebSocket transports cannot set custom headers, so the Angular client passes the
        // access token in the query string. Scoped to the hub path only.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/queue"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// ---------------------------------------------------------------------------
// Database bootstrap
// ---------------------------------------------------------------------------
// Applied on startup in Development so a fresh clone runs with one command.
// Production should apply migrations as a separate deployment step instead.
if (app.Environment.IsDevelopment())
{
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync();

        // Fully qualified: top-level statements sit in the global namespace, so the
        // FlashSale.Api namespace is not implicitly imported here.
        await FlashSale.Api.DatabaseSeeder.SeedAsync(app.Services);
    }
}

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Flash Sale API v1"));
}

// Must precede auth so CORS headers are present even on 401/403 responses.
app.UseCors(SpaCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// The SignalR hub. The client reaches this at /hubs/queue.
app.MapHub<QueueHub>("/hubs/queue");

app.MapGet("/health", () => Results.Ok(new { status = "healthy", utc = DateTimeOffset.UtcNow }))
   .AllowAnonymous()
   .WithName("HealthCheck");

app.Run();
