# Gorilla.IAM

The estate's identity and role-grant service (spec section 3, in the
GorillaHR repo's `specs/distributed-identity-architecture.md`). Issues
RS256-signed OIDC tokens via [OpenIddict](https://documentation.openiddict.com/)
and owns `subjects`, `credentials`, `role_grants` and `consumer_apps` — the
authoritative store neither app writes to directly.

Currently P1: this service exists and its data model, credential
verification, and OpenIddict wiring are real and tested, but nothing
consumes it yet (no client apps registered, no login UI, not reachable
through the gateway). "Dark," per the roadmap.

## Running it

```bash
cd src/Gorilla.IAM
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "server=localhost;port=3306;database=gorilla_iam;user=gorilla_iam_app;password=..."
dotnet run
# -> http://localhost:8100/health
# -> http://localhost:8100/.well-known/openid-configuration
```

No MySQL server is required to build, test, or generate migrations — see the
`ServerVersion` comment in `Program.cs` for why. It *is* required to actually
run the service, since `IamDbContext` isn't backed by anything else yet.

```bash
dotnet test                      # from iam/ — 38 tests, no DB needed
dotnet ef migrations add <Name>  # from src/Gorilla.IAM — needs a syntactically
                                  # valid connection string, not a live server
```

## Importing subjects from HR and RG

`tools/Gorilla.IAM.Import` reads both apps' user tables (read-only), plans
what to import (`src/Gorilla.IAM/Import/ImportPlanner.cs` — HR's credential
always wins when a person exists in both, per spec section 3.4), then either
reports or writes:

```bash
cd tools/Gorilla.IAM.Import
GORILLAHR_DB_PASSWORD=... RECRUITMENT_DB_PASSWORD=... dotnet run -- dry-run
#   ^ same env var names as gorilla-platform/scripts/reconcile_users.py.
#     Reports only — never writes to a database.

ConnectionStrings__DefaultConnection="..." dotnet run -- apply
#   ^ runs the same dry-run check first and refuses to write anything if it
#     fails (verified: killed one manifest password on purpose, confirmed
#     `apply` exits 1 and the subjects table stays at 0 rows). Re-running
#     `apply` is safe — it updates an existing subject if the source hash
#     changed, and touches nothing if it didn't.
```

The dry-run (spec section 9: "a dry-run import replaying known passwords
before the real one") only proves as much as the passwords in
`DryRunManifest.cs` are real and known — it defaults to this estate's seeded
dev accounts, which is right for proving the tool works, not for a real
cutover. A real dry-run needs a manifest of actual known-password test
accounts instead.

## Why net9.0 under a .NET 10 SDK

See the `TargetFramework` comment in `Gorilla.IAM.csproj`: `Pomelo.EntityFrameworkCore.MySql`
has no release supporting EF Core 10 yet, and `OpenIddict.EntityFrameworkCore`
ships a separate dependency group per target framework — its `net10.0` group
pins EF Core 10, its `net9.0` group pins EF Core 9. Targeting `net9.0` (with
`RollForward: LatestMajor`) makes NuGet resolve the dependency group that
actually agrees with Pomelo. Revisit once Pomelo catches up.

## Break-glass admin console

`/console` — spec section 3.1: "list subjects, toggle grants, deactivate,"
gated on a dedicated `iam:admin` grant. Authenticates directly against
Subject/Credential/RoleGrant (`Console/BreakGlassAuthenticator.cs`) via a
separate cookie scheme — deliberately **not** OpenIddict's own
authorization-code flow, since a break-glass path that only works when the
rest of the OIDC machinery also works isn't actually break-glass.

The bootstrap problem — the console can only grant `iam:admin` through a
form that itself requires `iam:admin` to reach — is closed by
`Iam:BootstrapAdminEmail`: on startup, if no one holds the grant yet, it's
given once to that email (matching RG's own `Auth:SeedAdminEmail` pattern).
The subject must already exist first — run the import tool, or create one
some other way.

```bash
cd src/Gorilla.IAM
ConnectionStrings__DefaultConnection="..." Iam__BootstrapAdminEmail="you@example.com" dotnet run
# -> http://localhost:8100/console/login
```

Verified against a real MySQL database end to end, not just unit tests:
bootstrap grant landing, login (cookie issued, correct redirect), the
dashboard rendering real imported subjects, granting and revoking a role
(with the granting admin's subject ID persisted), deactivate/reactivate,
a non-admin's correct password being rejected with the same generic message
a wrong password gets, and logout actually invalidating the session
(dashboard 200 before, 302 after). One real bug this caught: calling
`RequireAuthorization()` directly on the `/console` route group applied it
retroactively to every route already mapped on that group, including
`/console/login` itself — an unbreakable redirect loop. Fixed with a nested
`MapGroup("")` for the protected routes only; see the comment in
`ConsoleEndpoints.cs`.

### Admin-initiated password reset

The P2 blocker on the roadmap line: once Recruitment cuts over, a person
whose old RG password stops working and who doesn't know their HR password
(spec section 3.4's "one unsolvable case") has no self-service recovery
(out of scope until P6 — needs email neither app has). The dashboard's
"Reset password" column is the only way back in until then — an admin sets
a temporary password (`SubjectAdminService.ResetPasswordAsync`), which
flags `Credential.MustChangePassword`. No email is sent; communicating the
temporary password is the admin's job, out of band.

That flag gates the *console's own session*, not just a future OIDC flow —
spec section 3.2's "no token is issued until the password changes,"
applied here: logging in with a flagged credential returns an interim
session with no `admin` role claim, so every dashboard route correctly
denies it (`RequireRole`) and `AccessDeniedPath` sends it to
`/console/change-password` instead of back to the login form. Completing
the change (`BreakGlassAuthenticator.ChangePasswordAsync`, which
re-verifies the temporary password rather than trusting the interim
session alone) signs the session out entirely — sign in again with the new
password, no upgrade-in-place. Password rules
(`Auth/PasswordPolicy.cs`) are ported from GorillaHR's
`password_policy_error`, cross-checked against real output from that
function (byte length via UTF-8, not character count; CJK counts as a
letter) before being trusted, the same discipline as `Pbkdf2PasswordHasher`.

Verified end to end against real MySQL, including the two-request proof
that actually matters: `AccessDeniedPath` really does route the interim
session to `/console/change-password` (not a redirect loop back to the
denied page), and a full sign-in with the *old* temporary password fails
once the change completes while the *new* password reaches the dashboard.

## What's not here yet

- No Dockerfile, no compose service — see the top-level README.
- No OIDC login/consent UI, no registered OpenIddict clients for other
  apps to actually use yet — the break-glass console above authenticates
  itself directly and doesn't need either.
- Endpoint URL generation behind the eventual `/iam/` gateway prefix is an
  open question — see the `KNOWN GAP` comment in `Program.cs`. Don't trust
  the discovery document's URLs once a gateway route exists until that's
  resolved and tested against the real thing.
