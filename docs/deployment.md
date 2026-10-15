# Deployment runbook (single Linux VM)

The production-like stack (STORY-045, DEC-049) runs on one Linux VM with Docker Compose:
`infrastructure/production/docker-compose.yml`. HTTPS and public exposure are STORY-046; backups are STORY-047.

```
browser ──HTTP──▶ web (nginx :80, published on HTTP_BIND:HTTP_PORT)
                    ├─ /            static Angular app (SPA fallback to index.html)
                    ├─ /api/        REST ──▶ api:8080
                    └─ /api/hubs/   SignalR WebSocket ──▶ api:8080
                  api ──▶ postgres:5432 (volume postgres-data)
                  migrate (one-shot, before api) ──▶ postgres
                  redis (volume redis-data; not used by the API yet)
```

Only `web` publishes a port. The API, PostgreSQL and Redis sit on the internal `backend` network.

## VM prerequisites

- 64-bit Linux (2 vCPU / 4 GB RAM / 40–80 GB SSD to start), Docker Engine with the Compose plugin (v2.20+).
- A checkout of the repository at the release to deploy. Node.js and the .NET SDK aren't needed: the images build them.
- A firewall that allows SSH only, until HTTPS (STORY-046).

## First deployment

```bash
cp infrastructure/production/.env.example infrastructure/production/.env
chmod 600 infrastructure/production/.env
# Edit .env: set POSTGRES_PASSWORD (openssl rand -hex 24) and WALLET_AUTH_DOMAIN / WALLET_AUTH_URI.
docker compose -f infrastructure/production/docker-compose.yml up -d --build --wait
```

`up --wait` returns once PostgreSQL and Redis are healthy, the migrations have been applied (the `migrate` job
exited 0), the API answers `/health/ready` and nginx serves the app. Compose refuses to start when a required
setting is missing.

| Setting (`infrastructure/production/.env`) | Default | Notes |
|---|---|---|
| `POSTGRES_PASSWORD` | *(required)* | Letters and digits only (it goes into a connection string); `openssl rand -hex 24` |
| `WALLET_AUTH_DOMAIN` / `WALLET_AUTH_URI` | *(required)* | The web app's address as users see it; `http` only for `localhost` (DEC-015) |
| `POSTGRES_DB` / `POSTGRES_USER` | `choggames` | |
| `HTTP_BIND` / `HTTP_PORT` | `127.0.0.1` / `8080` | Where nginx listens on the VM |
| `MONAD_RPC_URL` | `https://rpc.monad.xyz` | A dedicated endpoint may carry a key; keep it in `.env` only |
| `CHOG_LIFE_STATUS` | `ComingSoon` | `Available` lists Chog Life as playable |
| `IMAGE_PREFIX` / `IMAGE_TAG` | `chog-game-arena` / `local` | Tag each release to roll back (below) |

Until STORY-046 adds HTTPS the session cookie is `Secure`, so browsers keep it only on `localhost`. Reach the app
through an SSH tunnel: `ssh -L 8080:127.0.0.1:8080 <vm>`, then open `http://localhost:8080`.

## Restarts and upgrades

- Containers restart on failure and after a reboot (`restart: unless-stopped`; enable the Docker service).
- `docker compose ... restart`, `down` and `up` keep all data: it lives in the `postgres-data` and `redis-data`
  volumes. **Never** run `down -v` on the VM; it deletes the database.
- Upgrade: check out the new release, then `IMAGE_TAG=<release> docker compose ... up -d --build --wait`.
  Migrations run before the new API starts; if they fail, Compose stops with an error before replacing the API.
- Roll back: `IMAGE_TAG=<previous release> docker compose ... up -d --wait`. Migrations aren't rolled back
  automatically, so a release that changes the schema needs its own rollback note.
- Matches in progress resume after an API restart (DEC-030); every active turn gets a fresh timer.

## Checks

- `docker compose -f infrastructure/production/docker-compose.yml ps` shows every service `healthy`, and `migrate` `exited (0)`.
- `curl -fsS http://127.0.0.1:8080/api/games` on the VM returns the catalog.
- Logs: `docker compose ... logs -f api web`.
- `npm run deploy:verify` (on a development machine or CI) builds and runs an isolated copy on port 18081, checks
  the app, the API, the WebSocket hub and that a guest survives recreating every container, then removes it.
