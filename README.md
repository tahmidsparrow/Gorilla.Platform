# Gorilla.Platform

Shared infrastructure for the Gorilla estate (GorillaHR + Recruitment.Gorilla):
the Docker Compose topology and nginx gateway that put both apps under one
origin, and the shared identity service, Gorilla.IAM.

```
deploy/   compose.yaml, nginx config, MySQL bootstrap        (P0)
iam/      Gorilla.IAM — .NET 9 + EF Core + OpenIddict         (P1, in progress)
```

See [`deploy/README.md`](deploy/README.md) to run the P0 stack locally — it
requires this repo, `GorillaHR`, and `Recruitment.Gorilla` checked out as
siblings under one parent directory. `iam/` is not wired into `deploy/`
yet — it builds, runs, and its tests pass standalone (`cd iam && dotnet test`),
but it has no Dockerfile and no compose service, so the gateway does not yet
route to it (see the `/iam/*` comment in `deploy/nginx/nginx.conf`).

Full architecture and phased roadmap: `specs/distributed-identity-architecture.md`
in the GorillaHR repo.
