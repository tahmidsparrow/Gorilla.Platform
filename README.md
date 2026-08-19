# Gorilla.Platform

Shared infrastructure for the Gorilla estate (GorillaHR + Recruitment.Gorilla):
the Docker Compose topology and nginx gateway that put both apps under one
origin, and — starting in a later phase — the shared identity service.

```
deploy/     compose.yaml, nginx config, MySQL bootstrap   (this phase)
identity/   the identity service (.NET 10 + OpenIddict)   (a later phase)
```

See [`deploy/README.md`](deploy/README.md) to run the stack locally — it
requires this repo, `GorillaHR`, and `Recruitment.Gorilla` checked out as
siblings under one parent directory.

Full architecture and phased roadmap: `specs/distributed-identity-architecture.md`
in the GorillaHR repo.
