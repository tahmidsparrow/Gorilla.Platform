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
dotnet test                      # from iam/ — 14 tests, no DB needed
dotnet ef migrations add <Name>  # from src/Gorilla.IAM — needs a syntactically
                                  # valid connection string, not a live server
```

## Why net9.0 under a .NET 10 SDK

See the `TargetFramework` comment in `Gorilla.IAM.csproj`: `Pomelo.EntityFrameworkCore.MySql`
has no release supporting EF Core 10 yet, and `OpenIddict.EntityFrameworkCore`
ships a separate dependency group per target framework — its `net10.0` group
pins EF Core 10, its `net9.0` group pins EF Core 9. Targeting `net9.0` (with
`RollForward: LatestMajor`) makes NuGet resolve the dependency group that
actually agrees with Pomelo. Revisit once Pomelo catches up.

## What's not here yet

- No Dockerfile, no compose service — see the top-level README.
- No login/consent UI, no registered OpenIddict clients, no break-glass
  admin console — separate P1 deliverables per the roadmap.
- Endpoint URL generation behind the eventual `/iam/` gateway prefix is an
  open question — see the `KNOWN GAP` comment in `Program.cs`. Don't trust
  the discovery document's URLs once a gateway route exists until that's
  resolved and tested against the real thing.
