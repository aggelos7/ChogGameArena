# Deployment runbook (single Linux VM)

The production-like stack (STORY-045, DEC-049) runs on one Linux VM with Docker Compose:
`infrastructure/production/docker-compose.yml`. HTTPS is STORY-046 (DEC-050); backups are STORY-047.

```
browser ──HTTPS──▶ web (nginx :443, for PUBLIC_HOSTNAME only)
                    ├─ /            static Angular app (SPA fallback to index.html)
                    ├─ /api/        REST ──▶ api:8080
                    └─ /api/hubs/   SignalR WebSocket (wss) ──▶ api:8080
        ──HTTP───▶ web (nginx :80): /.well-known/acme-challenge/ from the acme-webroot volume, else 301 to https
                  certbot (letsencrypt profile) ──▶ Let's Encrypt; writes volumes letsencrypt + acme-webroot
                  api ──▶ postgres:5432 (volume postgres-data)
                  migrate (one-shot, before api) ──▶ postgres
                  redis (volume redis-data; not used by the API yet)
```

Only `web` publishes ports (80 and 443). The API, PostgreSQL and Redis sit on the internal `backend` network.

## VM prerequisites

- 64-bit Linux (2 vCPU / 4 GB RAM / 40–80 GB SSD to start), Docker Engine with the Compose plugin (v2.20+).
- A checkout of the repository at the release to deploy. Node.js and the .NET SDK aren't needed: the images build them.
- A DNS name for the app (`PUBLIC_HOSTNAME`, e.g. `play.<domain>`) with an `A` (and `AAAA`, if the VM has IPv6)
  record pointing at the VM, before the first start, so Let's Encrypt can validate it.
- A firewall that allows SSH, 80 and 443 only. Port 80 must stay open: certificate renewals are validated
  over it.

## First deployment

```bash
cp infrastructure/production/.env.example infrastructure/production/.env
chmod 600 infrastructure/production/.env
# Edit .env: set POSTGRES_PASSWORD (openssl rand -hex 24) and PUBLIC_HOSTNAME; keep COMPOSE_PROFILES=letsencrypt.
docker compose -f infrastructure/production/docker-compose.yml up -d --build --wait
docker compose -f infrastructure/production/docker-compose.yml logs -f certbot web   # until the certificate is issued
```

`up --wait` returns once PostgreSQL and Redis are healthy, the migrations have been applied (the `migrate` job
exited 0), the API answers `/health/ready` and nginx is up. Compose refuses to start when a required setting is
missing.

nginx starts on a self-signed certificate (browsers warn), so it can answer the challenge; certbot then obtains
the real one, and nginx switches to it within a minute (`40-chog-tls.sh: now serving ...` in the `web` log). If
issuance fails (DNS not pointing at the VM yet, port 80 blocked), certbot logs why and retries every hour; Let's
Encrypt allows 5 failed validations per host per hour.

| Setting (`infrastructure/production/.env`) | Default | Notes |
|---|---|---|
| `POSTGRES_PASSWORD` | *(required)* | Letters and digits only (it goes into a connection string); `openssl rand -hex 24` |
| `PUBLIC_HOSTNAME` | *(required)* | The one host name served (DEC-013, DEC-050); others are refused |
| `COMPOSE_PROFILES` | `letsencrypt` in `.env.example` | Runs certbot; without it nginx keeps a self-signed certificate |
| `CERTBOT_EMAIL` | *(none)* | Optional Let's Encrypt account contact |
| `WALLET_AUTH_DOMAIN` / `WALLET_AUTH_URI` | `PUBLIC_HOSTNAME` / `https://PUBLIC_HOSTNAME` | Wallet sign-in address (DEC-015); override only for a non-standard port |
| `POSTGRES_DB` / `POSTGRES_USER` | `choggames` | |
| `HTTP_BIND` / `HTTP_PORT` / `HTTPS_PORT` | `0.0.0.0` / `80` / `443` | Where nginx listens on the VM |
| `MONAD_RPC_URL` | `https://rpc.monad.xyz` | A dedicated endpoint may carry a key; keep it in `.env` only |
| `CHOG_LIFE_STATUS` | `ComingSoon` | `Available` lists Chog Life as playable |
| `IMAGE_PREFIX` / `IMAGE_TAG` | `chog-game-arena` / `local` | Tag each release to roll back (below) |

## HTTPS and certificates

- Every HTTPS response sends HSTS for one year (this host only: no `includeSubDomains`, no `preload`) and
  `Content-Security-Policy: upgrade-insecure-requests`. Once a browser has seen HSTS it refuses plain HTTP and
  certificate warnings for the host for a year, so get the real certificate working before sharing the address.
- Renewal is automatic: certbot checks twice a day and renews within 30 days of expiry; nginx reloads within a
  minute. Check with `docker compose ... exec certbot certbot certificates`.
- Changing `PUBLIC_HOSTNAME`: update DNS first, then `docker compose ... exec certbot certbot delete --cert-name chog`
  and `docker compose ... up -d --wait`; certbot obtains a certificate for the new name.
- Using a certificate from elsewhere (no certbot): leave `letsencrypt` out of `COMPOSE_PROFILES` and copy
  `fullchain.pem` and `privkey.pem` into the `letsencrypt` volume at `live/chog/` (e.g. with a throwaway
  container mounting it); nginx picks them up within a minute. Renewing them is then up to you.
- The certificate and the Let's Encrypt account key live in the `letsencrypt` volume; `down` keeps it. Losing it
  only means certbot obtains a new certificate (mind the rate limit of 5 per host per week).

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
- `curl -fsS https://<PUBLIC_HOSTNAME>/api/games` returns the catalog; `curl -sI http://<PUBLIC_HOSTNAME>/` is a
  `301` to `https://`.
- Logs: `docker compose ... logs -f api web`.
- `npm run deploy:verify` (on a development machine or CI) builds and runs an isolated copy for `localhost` on
  ports 18081/18443 (without certbot). Over TLS verified against the served certificate it checks the app, the
  API, the wss hub, the HSTS and CSP headers, the HTTP redirect, the refusal of other host names, the switch to a
  stand-in issued certificate, and that a guest and the certificate survive recreating every container. Then it
  removes the copy.
