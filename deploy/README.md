# deploy/

Docker Compose stack for the Gorilla estate: `hr-api` / `hr-web` (GorillaHR),
`ats-api` / `ats-web` (Recruitment.Gorilla), `mysql`, and the `gateway`
(nginx) that puts them all under one origin via path prefixes (`/hr`, `/ats`;
`/id` lands in P1 once the identity service exists).

## Sibling-checkout contract

`compose.yaml` builds each app image from a **relative path into a sibling
repo**:

```
../../GorillaHR            (context for hr-api / hr-web)
../../Recruitment.Gorilla  (context for ats-api / ats-web)
```

That means this repo, `GorillaHR`, and `Recruitment.Gorilla` must be checked
out as siblings under the same parent directory:

```
<parent>/
├── GorillaHR/
├── Recruitment.Gorilla/
└── gorilla-platform/
```

This is the local-dev and CI contract until each app publishes tagged images
to a registry (see the top-level README's roadmap note) — at that point
`compose.yaml` switches from `build:` to `image:` and the sibling-checkout
requirement goes away.

## Running it

```bash
cp .env.example .env   # fill in real values — never commit .env
docker compose up -d --build
curl http://localhost/hr/api/health
```

Each app's own `Dockerfile` lives in that app's repo, next to the code it
builds — never here. This directory only holds the things that belong to
neither app alone: the gateway config, the compose topology, and the
MySQL bootstrap script (`mysql-init/`).
