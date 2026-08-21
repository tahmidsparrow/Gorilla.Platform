using Gorilla.IAM.Console;
using Gorilla.IAM.Data;
using Gorilla.IAM.Oidc;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connStr))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not configured. Set it via " +
        "user secrets (dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"...\") " +
        "or the ConnectionStrings__DefaultConnection environment variable.");

// A fixed ServerVersion (matching the mysql:9 image in deploy/compose.yaml)
// rather than ServerVersion.AutoDetect(connStr) — AutoDetect opens a real
// connection at startup, which also means at `dotnet ef migrations add` time.
// A fixed version keeps migration generation possible with no database
// running, the same way GorillaHR's Alembic and this repo's CI both work
// without a live server until the actual `docker compose up` step.
var serverVersion = new MySqlServerVersion(new Version(9, 0, 0));

builder.Services.AddDbContext<IamDbContext>(options =>
    options.UseMySql(connStr, serverVersion));

builder.Services.AddScoped<BreakGlassAuthenticator>();
builder.Services.AddScoped<SubjectAdminService>();

// A separate, named cookie scheme for the break-glass console — never the
// default scheme, and never touched by OpenIddict's own AddValidation()
// below. Deliberately does not depend on OpenIddict at all: see
// BreakGlassAuthenticator's class doc for why a break-glass path must not
// share fate with the machinery it exists to work around.
builder.Services.AddAuthentication()
    .AddCookie(ConsoleAuth.Scheme, options =>
    {
        options.Cookie.Name = "gorilla_iam_console";
        options.LoginPath = "/console/login";
        // Not "/console/login": the only two states a cookie from this
        // scheme can be in are "full admin" (Role claim present) or "must
        // change password" (no Role claim, set on MustChangePassword
        // sign-in — see ConsoleEndpoints). AccessDenied only fires for an
        // *authenticated* principal that fails a policy, so landing here
        // always means the latter state; sending them anywhere else would
        // be a dead end with no way to reach the one page that unblocks them.
        options.AccessDeniedPath = "/console/change-password";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services
    .AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore().UseDbContext<IamDbContext>();
    })
    .AddServer(options =>
    {
        options
            .SetAuthorizationEndpointUris("connect/authorize")
            .SetTokenEndpointUris("connect/token")
            .SetUserInfoEndpointUris("connect/userinfo")
            .SetEndSessionEndpointUris("connect/logout")
            .SetIntrospectionEndpointUris("connect/introspect");

        // Authorization code + PKCE, matching the spec's oidc-client-ts plan for
        // both SPAs (section 3.4) — never the resource-owner password grant,
        // deprecated in OAuth 2.1 and exactly what this service replaces.
        options
            .AllowAuthorizationCodeFlow()
            .RequireProofKeyForCodeExchange()
            .AllowRefreshTokenFlow();

        options.RegisterScopes(
            Scopes.OpenId,
            Scopes.Email,
            Scopes.Profile,
            Scopes.OfflineAccess);

        // https://gorilla.test/iam in production (spec section 3.2); overridable
        // per environment since this container has no idea it sits behind a
        // gateway path prefix otherwise — see HR's UVICORN_ROOT_PATH for the
        // same problem solved a different way.
        var issuer = builder.Configuration["Iam:Issuer"];
        if (!string.IsNullOrWhiteSpace(issuer))
            options.SetIssuer(new Uri(issuer, UriKind.Absolute));

        if (builder.Environment.IsDevelopment())
        {
            // Ephemeral keys regenerated on every restart — every session drops
            // on redeploy. Fine for a service that is still "dark" (spec P1);
            // real certificates land before anything in P2/P3 depends on this.
            options.AddDevelopmentEncryptionCertificate();
            options.AddDevelopmentSigningCertificate();

            // OpenIddict refuses plain HTTP by default. The real deployment
            // (gateway/deploy/nginx.conf) terminates TLS at the gateway and
            // talks to this container over plain HTTP — exactly like hr-api
            // and ats-api — which ForwardedHeadersMiddleware below handles
            // correctly. This bypass exists only so `dotnet run` works for
            // local iteration with no gateway in front of it at all.
            options.UseAspNetCore().DisableTransportSecurityRequirement();
        }

        options
            .UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

// Trusts X-Forwarded-Proto/-For so Request.IsHttps and Request.Scheme reflect
// what the browser actually used, not the plain-HTTP hop from the gateway.
// KnownNetworks/KnownProxies are cleared rather than left at their loopback-only
// default: the gateway reaches this container over the "gorilla" Docker bridge
// network, whose address is not fixed in advance, and app containers already
// publish no host ports (see AGENTS.md's loopback-only rule), so the gateway
// is the only thing that can reach this container at all.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// KNOWN GAP, deliberately not solved here: once the gateway has an actual
// /iam/ route (see nginx.conf's comment — it strips the prefix before
// proxying, same as hr-api under /hr), OpenIddict's discovery document and
// generated endpoint URLs still need to carry "/iam" for external clients,
// and this container never sees that prefix on the incoming path to derive
// it from. Two candidates, neither verified against a real proxy yet:
// ForwardedHeaders.XForwardedPrefix (nginx sending X-Forwarded-Prefix: /iam),
// or configuring OpenIddict's endpoint URIs as absolute paths built from
// Iam:Issuer directly. A manual app.UsePathBase("/iam") does NOT work —
// tried it: ASP.NET Core's own routing saw the stripped path fine (/health
// resolved), but OpenIddict's discovery endpoint still 404'd, because
// OpenIddict.Server.AspNetCore inserts its request handling via an
// IStartupFilter that runs before any app.Use(...) call in this file can.
// Revisit when the nginx route + compose service are added and this can be
// tested against the real thing instead of curl simulations.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.MapConsoleEndpoints();
app.MapOidcEndpoints();

// Liveness only — this service owns no other apps' health, so unlike HR's
// /api/health this does not aggregate anything. No /api prefix: everything
// this service exposes is either an OpenIddict /connect/* endpoint or its
// own admin API, never a proxied domain API.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Idempotent — matches Recruitment.Gorilla's own startup-seed pattern
// (Program.cs: seed the Super Admin "if !await db.Users.AnyAsync()").
// Deliberately does NOT run migrations first: unlike RG, which migrates on
// every boot, HR's split (a one-shot hr-migrate service, migrate-on-boot
// disabled on the API containers via RUN_MIGRATIONS=0) is the intended model
// here too — see the RUN_MIGRATIONS comment in GorillaHR's
// docker-entrypoint.sh. No such split exists for this service yet (no
// Dockerfile), so for now this seed step simply fails loudly if the
// consumer_apps table doesn't exist — apply migrations by hand
// (`dotnet ef database update`) before running.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Gorilla.IAM.Data.IamDbContext>();
    await Gorilla.IAM.Data.Seeding.ConsumerAppSeeder.SeedAsync(db);

    // Closes the break-glass console's own bootstrap problem: the console
    // can only grant iam:admin through a form that itself requires
    // iam:admin to reach. Grants it once, to whoever Iam:BootstrapAdminEmail
    // names, the same "seed the first admin from config" idiom RG itself
    // uses (Program.cs, Auth:SeedAdminEmail) — see BootstrapAdminSeeder.
    var bootstrapWarning = await Gorilla.IAM.Data.Seeding.BootstrapAdminSeeder.SeedAsync(
        db, builder.Configuration["Iam:BootstrapAdminEmail"]);
    if (bootstrapWarning is not null)
        app.Logger.LogWarning("{Warning}", bootstrapWarning);

    // P2 increment 1: registers Recruitment.Gorilla's SPA as an OpenIddict
    // client — a different table from ConsumerApps above; see
    // OpenIddictClientSeeder's doc comment for why both exist.
    var applicationManager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
    var clientWarning = await Gorilla.IAM.Data.Seeding.OpenIddictClientSeeder.SeedAsync(
        applicationManager, builder.Configuration["Iam:AtsClientRedirectUris"]);
    if (clientWarning is not null)
        app.Logger.LogWarning("{Warning}", clientWarning);
}

app.Run();

// Required for WebApplicationFactory-based integration tests in
// Gorilla.IAM.Tests to reference the entry point.
public partial class Program;
