// Eminova.Host — Program.cs
// ASP.NET Core 8 minimal API that hosts the EMINOVA HR & Payroll Workbench on any server
// (Windows/IIS, Linux/Nginx, Docker, Azure App Service, AWS) and gives it server-side
// persistence, authentication, health checks and a customer-dataset import endpoint.
//
// Endpoints
//   GET    /                       dashboard (wwwroot/index.html, patched by Eminova.Patch)
//   GET    /healthz                liveness + database check (used by load balancer / monitoring)
//   GET    /api/version            build, database provider and auth mode
//   GET    /api/state              the signed-in user's dashboard state (204 = none yet -> dashboard seeds)
//   PUT    /api/state              save state (dashboard calls this from save())
//   POST   /api/state              same as PUT (navigator.sendBeacon on page unload)
//   DELETE /api/state              reset to seed data
//   GET    /api/import/entities    list of importable datasets
//   POST   /api/import/{entity}    multipart 'file' (CSV or JSON) merged into state
//   GET    /api/audit              last 200 audit entries for the tenant

using System.Text.Json;
using Eminova.Host.Data;
using Eminova.Host.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------------------------------------------------------------- 1. Database (provider chosen in appsettings)
var provider = (cfg["Database:Provider"] ?? "Sqlite").Trim();
var connectionString = cfg.GetConnectionString("Eminova") ?? "Data Source=App_Data/eminova.db";

builder.Services.AddDbContext<EminovaDbContext>(options =>
{
    switch (provider.ToLowerInvariant())
    {
        case "postgresql": options.UseNpgsql(connectionString); break;
        case "sqlserver":  options.UseSqlServer(connectionString); break;
        default:           options.UseSqlite(connectionString); break;   // POC / single-server default
    }
});

// ---------------------------------------------------------------- 2. Identity (OIDC when Auth:Authority is configured)
var authEnabled = !string.IsNullOrWhiteSpace(cfg["Auth:Authority"]);
if (authEnabled)
{
    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = "eminova.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(10);
            options.SlidingExpiration = true;
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = cfg["Auth:Authority"];          // e.g. https://id.<client>.co.za/realms/eminova  or  https://login.microsoftonline.com/<tenant>/v2.0
            options.ClientId = cfg["Auth:ClientId"];
            options.ClientSecret = cfg["Auth:ClientSecret"];     // keep in user-secrets / environment, never in source control
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.TokenValidationParameters.NameClaimType = cfg["Auth:NameClaim"] ?? "preferred_username";
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";
        });

    // Every endpoint requires a signed-in user unless marked AllowAnonymous (health check).
    builder.Services.AddAuthorization(options => options.FallbackPolicy = options.DefaultPolicy);
}

builder.Services.AddHealthChecks().AddDbContextCheck<EminovaDbContext>("database");
builder.Services.AddScoped<DatasetImporter>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind Nginx / IIS ARR / Azure Front Door the proxy terminates TLS; trust its headers.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// ---------------------------------------------------------------- 3. Schema (EnsureCreated for POC; EF migrations for production releases)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EminovaDbContext>();
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "App_Data"));
    await db.Database.EnsureCreatedAsync();
}

// ---------------------------------------------------------------- 4. Pipeline
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
if (cfg.GetValue("Hosting:HttpsRedirect", true)) app.UseHttpsRedirection();

app.Use(async (ctx, next) =>
{
    // Baseline security headers (the dashboard is a single self-contained page; fonts come from Google Fonts).
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "SAMEORIGIN";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; script-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'";
    await next();
});

// Note: no UseDefaultFiles() — '/' is served by the MapFallbackToFile endpoint below so that, when OIDC is
// enabled, the authorization fallback policy challenges the user with a full-page redirect before the
// dashboard loads. Static assets (eminova-api.js, fonts) stay anonymous.
app.UseStaticFiles();
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

string TenantOf(HttpContext ctx) =>
    ctx.Request.Headers["X-Eminova-Tenant"].FirstOrDefault()
    ?? ctx.Request.Query["tenant"].FirstOrDefault()
    ?? cfg["Hosting:DefaultTenant"] ?? "default";

string UserOf(HttpContext ctx) =>
    ctx.User?.Identity?.IsAuthenticated == true ? (ctx.User.Identity!.Name ?? "unknown") : "poc-user";

// Payroll needs ONE shared state per tenant: the preparer, approver and treasury authoriser must act on the same run
// (segregation of duties / four-eyes). Hosting:StateScope = "tenant" (default) shares it; "user" gives each user a sandbox (demo/UAT).
var stateScope = (cfg["Hosting:StateScope"] ?? "tenant").Trim().ToLowerInvariant();
string StateKeyOf(HttpContext ctx) => stateScope == "user" ? UserOf(ctx) : "tenant-shared";

// ---------------------------------------------------------------- 5. Operational endpoints
app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapMethods("/error", new[] { "GET", "POST", "PUT", "DELETE" },
    () => Results.Problem("An unexpected error occurred. Check the Eminova.Host log.")).AllowAnonymous();
app.MapGet("/api/version", () => Results.Ok(new
{
    app = "Eminova.Host",
    version = typeof(Program).Assembly.GetName().Version?.ToString(3),
    database = provider,
    auth = authEnabled ? "oidc" : "poc-anonymous",
    stateScope,
    utc = DateTime.UtcNow
})).AllowAnonymous();

// ---------------------------------------------------------------- 6. State API (replaces localStorage)
app.MapGet("/api/state", async (HttpContext ctx, EminovaDbContext db) =>
{
    var row = await db.TenantStates.FindAsync(TenantOf(ctx), StateKeyOf(ctx));
    if (row is null) return Results.NoContent();
    ctx.Response.Headers.ETag = $"\"{row.Version}\"";            // optimistic concurrency token for the shim
    return Results.Content(row.Json, "application/json");
});

async Task<IResult> SaveStateAsync(HttpContext ctx, EminovaDbContext db)
{
    const int maxBytes = 25_000_000;                      // measured: 31 KB seed state; ~1–2 KB per employee incl. run results
    using var reader = new StreamReader(ctx.Request.Body);
    var json = await reader.ReadToEndAsync();
    if (json.Length == 0) return Results.BadRequest("Empty body.");
    if (json.Length > maxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    try { using var _ = JsonDocument.Parse(json); }          // reject malformed JSON before it reaches the database
    catch (JsonException ex) { return Results.BadRequest($"Invalid JSON: {ex.Message}"); }

    var tenant = TenantOf(ctx);
    var user = UserOf(ctx);
    var row = await db.TenantStates.FindAsync(tenant, StateKeyOf(ctx));
    // Two users editing the same tenant: the shim sends If-Match with the version it loaded. A stale version gets 409,
    // the shim reloads the latest state and tells the user (no silent overwrite of another person's payroll action).
    var ifMatch = ctx.Request.Headers.IfMatch.FirstOrDefault()?.Trim('"');
    if (row is not null && !string.IsNullOrEmpty(ifMatch) && ifMatch != row.Version.ToString())
        return Results.Conflict(new { current = row.Version, yours = ifMatch, message = "State changed since you loaded it." });
    if (row is null)
    {
        row = new TenantState { TenantId = tenant, UserId = StateKeyOf(ctx) };
        db.TenantStates.Add(row);
    }
    row.Json = json;
    row.Version += 1;
    row.UpdatedUtc = DateTime.UtcNow;
    db.AuditEntries.Add(new AuditEntry
    {
        TenantId = tenant, UserId = user, Action = "state.save",
        Detail = $"v{row.Version} · {json.Length:N0} bytes", AtUtc = row.UpdatedUtc
    });
    await db.SaveChangesAsync();
    ctx.Response.Headers.ETag = $"\"{row.Version}\"";
    return Results.Ok(new { row.Version, row.UpdatedUtc });
}
app.MapPut("/api/state", SaveStateAsync);
app.MapPost("/api/state", SaveStateAsync);

app.MapDelete("/api/state", async (HttpContext ctx, EminovaDbContext db) =>
{
    var row = await db.TenantStates.FindAsync(TenantOf(ctx), StateKeyOf(ctx));
    if (row is not null)
    {
        db.TenantStates.Remove(row);
        db.AuditEntries.Add(new AuditEntry { TenantId = row.TenantId, UserId = row.UserId, Action = "state.reset", Detail = "state deleted – dashboard will reseed", AtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
});

// ---------------------------------------------------------------- 7. Customer dataset import
app.MapGet("/api/import/entities", () => Results.Ok(DatasetImporter.Specs.Values.Select(s => new { s.Entity, s.StateKey, s.IdPrefix, columns = s.Columns.Keys })));

app.MapPost("/api/import/{entity}", async (string entity, HttpContext ctx, DatasetImporter importer) =>
{
    if (!ctx.Request.HasFormContentType) return Results.BadRequest("Send multipart/form-data with a 'file' field (CSV or JSON).");
    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest("Upload the dataset as multipart field 'file'.");
    try
    {
        var result = await importer.ImportAsync(TenantOf(ctx), StateKeyOf(ctx), entity, file);
        return Results.Ok(result);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

app.MapGet("/api/audit", async (HttpContext ctx, EminovaDbContext db) =>
{
    var tenant = TenantOf(ctx);
    var rows = await db.AuditEntries.Where(a => a.TenantId == tenant)
        .OrderByDescending(a => a.AtUtc).Take(200).ToListAsync();
    return Results.Ok(rows);
});

app.MapFallbackToFile("index.html");   // deep links such as /#quality still load the dashboard
app.Run();
