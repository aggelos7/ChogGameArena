# Deployment runbook (single Linux VM)

The production-like stack (STORY-045, DEC-049) runs on one Linux VM with Docker Compose:
`infrastructure/production/docker-compose.yml`. HTTPS is STORY-046 (DEC-050); backups and restore are STORY-047
(DEC-051).

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
                  backup (backup profile) ──▶ postgres; volume backups ──rclone──▶ S3-compatible bucket (off-VM)
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
# Edit .env: set POSTGRES_PASSWORD (openssl rand -hex 24) and PUBLIC_HOSTNAME; keep COMPOSE_PROFILES=letsencrypt,backup
# and set the backup key and bucket (see "Backups and restore" below).
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
| `COMPOSE_PROFILES` | `letsencrypt,backup` in `.env.example` | Runs certbot (without it nginx keeps a self-signed certificate) and the backups |
| `BACKUP_AGE_RECIPIENT`, `BACKUP_S3_*` | *(required by the backup profile)* | See [Backups and restore](#backups-and-restore) |
| `CERTBOT_EMAIL` | *(none)* | Optional Let's Encrypt account contact |
| `WALLET_AUTH_DOMAIN` / `WALLET_AUTH_URI` | `PUBLIC_HOSTNAME` / `https://PUBLIC_HOSTNAME` | Wallet sign-in address (DEC-015); override only for a non-standard port |
| `POSTGRES_DB` / `POSTGRES_USER` | `choggames` | |
| `HTTP_BIND` / `HTTP_PORT` / `HTTPS_PORT` | `0.0.0.0` / `80` / `443` | Where nginx listens on the VM |
| `MONAD_RPC_URL` | `https://rpc.monad.xyz` | A dedicated endpoint may carry a key; keep it in `.env` only |
| `CHOG_LIFE_STATUS` | `ComingSoon` | `Available` lists Chog Life as playable |
| `CHOG_RACE_STATUS` | `ComingSoon` | `Available` lists Chog Race as playable (DEC-061) |
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

## Backups and restore

The `backup` service (STORY-047, DEC-051) backs up the database every day at `BACKUP_TIME` (03:00 UTC):

1. `pg_dump --format=custom` of the `choggames` database, piped straight into `age`, which encrypts it to
   `BACKUP_AGE_RECIPIENT`. The unencrypted dump is never written to disk.
2. The archive `choggames-<YYYYMMDDTHHMMSSZ>.dump.age` (mode 600) goes into the `backups` volume.
3. rclone copies it to `BACKUP_S3_BUCKET/BACKUP_S3_PREFIX` (the off-VM copy).
4. Retention runs on the VM and in the bucket alike: it keeps the 7 newest archives and the newest archive of
   each of the 4 newest weeks (weeks start on Monday). Everything older is deleted, so about a month of
   history is kept. Pruning runs only after a successful upload.

If any step fails, the service logs `ERROR` and retries an hour later. If the upload fails, the archive stays on
the VM and nothing is pruned. This means you can lose up to a day of data (RPO 24 hours). A restore takes
minutes.

**What an archive contains:** only the database: players, sessions, rooms, matches and results. Session
tokens are stored hashed, so they can't be used to sign in. An archive never contains:

- `.env` or any of its values;
- PostgreSQL role passwords (it's `pg_dump`, not `pg_dumpall`);
- the TLS certificate or its key;
- the age private key;
- the bucket key.

Redis holds nothing yet, so it isn't backed up. `npm run backup:verify` checks all of this.

### Setting up backups

1. **Create the key pair off the VM** (on your machine, with [age](https://age-encryption.org) installed):
   `age-keygen -o chog-backup-key.txt`. It prints the public key (`age1...`). Store
   `chog-backup-key.txt` in two safe places, such as a password manager and an offline copy. **Never put it on
   the VM.** Without it no backup can be restored. Anyone who has it can read every backup.
2. **Create the bucket** at any S3-compatible provider, plus an access key limited to that bucket that can
   put, get, list and delete objects. Turning on versioning, with noncurrent versions expiring after about 30
   days, means that someone who takes over the VM can't destroy the history with the key.
3. **Fill in `infrastructure/production/.env`:**
   - `BACKUP_AGE_RECIPIENT`: the public key.
   - `BACKUP_S3_BUCKET`, `BACKUP_S3_ENDPOINT`, `BACKUP_S3_ACCESS_KEY_ID` and `BACKUP_S3_SECRET_ACCESS_KEY`.
   - `BACKUP_S3_REGION` and `BACKUP_S3_PROVIDER` (rclone's S3 provider name, e.g. `AWS`, `Cloudflare` or
     `Wasabi`; default `Other`), if your provider needs them.
   - Keep `backup` in `COMPOSE_PROFILES`.
4. Start it and take a first backup to check the settings:

```bash
C="docker compose -f infrastructure/production/docker-compose.yml"
$C up -d --build --wait
$C run --rm backup now      # "wrote ...", "copied ... off-VM"
$C run --rm backup list     # the archives on the VM and in the bucket
$C logs backup              # "next backup in ...s (daily at 03:00 UTC)"
```

The service won't start without a valid `BACKUP_AGE_RECIPIENT` or a `BACKUP_S3_BUCKET`, with a malformed `BACKUP_TIME`, or
when it can't reach the bucket (wrong endpoint or key). It then never becomes healthy, so `up --wait` fails rather
than leaving the stack running without backups. Check `$C logs backup` for `ERROR` lines, for example in a weekly look at the logs, until
monitoring (STORY-048) alerts on them.

| Setting | Default | Notes |
|---|---|---|
| `BACKUP_AGE_RECIPIENT` | *(required)* | age public key (`age1...`); the private key stays off the VM |
| `BACKUP_S3_BUCKET` / `BACKUP_S3_ENDPOINT` | *(required)* | The off-VM bucket and its S3 endpoint URL |
| `BACKUP_S3_ACCESS_KEY_ID` / `BACKUP_S3_SECRET_ACCESS_KEY` | *(required)* | Limited to the bucket |
| `BACKUP_S3_REGION` / `BACKUP_S3_PROVIDER` | *(none)* / `Other` | rclone S3 options |
| `BACKUP_S3_PREFIX` | `chog-game-arena` | Folder in the bucket |
| `BACKUP_TIME` | `03:00` | Daily, `HH:MM` UTC |
| `BACKUP_KEEP_DAILY` / `BACKUP_KEEP_WEEKLY` | `7` / `4` | Retention, on the VM and off-VM |

### Restoring

A restore always goes into an **empty** database: a new VM, or a stack whose `postgres-data` volume has been
removed. A restore into a database that already has tables is refused.

The private key is piped in on standard input from your own machine. It is never stored on the VM, even
temporarily outside the throwaway restore container.

1. On the VM, check out the release that made the backup (or a later one). Create `.env` with the same
   settings: the bucket and the public key. The `POSTGRES_PASSWORD` may be new.
2. **Only if you are replacing a broken database on the same VM:**
   1. Take a last backup if the database still works (`$C run --rm backup now`).
   2. Run `$C down`.
   3. Run `docker volume rm chog-game-arena-prod_postgres-data`. This deletes the database.
3. Start PostgreSQL alone, so the migrations don't create an empty schema first:
   `$C up -d --wait postgres`.
4. Pick an archive with `$C run --rm backup list`, or use `latest` (the newest archive on the VM or in the
   bucket).
5. Restore from your machine, piping the key in:

```bash
ssh <vm> 'cd <checkout> && docker compose -f infrastructure/production/docker-compose.yml run --rm -T backup restore latest' \
  < chog-backup-key.txt
```

   The restore fetches the archive from the VM, or from the bucket if it isn't on the VM. It decrypts the
   archive in the container and loads it with `pg_restore --single-transaction --exit-on-error`. A wrong key, a
   damaged or altered archive (age authenticates every chunk), or any load error stops it before the database
   changes, or rolls the load back. It prints `restored <archive>` when done.
6. Start the rest: `$C up -d --build --wait`. The `migrate` job applies only the migrations that are newer than
   the backup. Matches that were active at backup time resume (DEC-030).
7. Check: `curl -fsS https://<PUBLIC_HOSTNAME>/api/games`, and that a known player can still sign in.
   Players keep their sessions as of the backup. Anything after it is lost.

### Restore drill

`npm run backup:verify` (in CI on every change) runs the whole cycle on an isolated stack, with a local
stand-in for the bucket:

- backs up live data;
- checks the archive is encrypted and holds no secrets;
- checks retention in both places;
- checks that a successful backup pings a stand-in heartbeat monitor without logging its URL;
- deletes the stack;
- refuses a wrong key, a damaged archive and a non-empty database;
- restores from the off-VM copy into a clean stack;
- checks that a guest's session still works.

Also restore a real off-VM archive once a quarter, and after changing the bucket or the key. Use a throwaway
project, which gets its own empty volumes and publishes no ports when only PostgreSQL runs. Run this on any
machine with Docker, a checkout and a copy of `.env` (the bucket settings). On the VM, pipe the key over
`ssh` as in step 5 above.

```bash
D="docker compose -p chog-restore-drill -f infrastructure/production/docker-compose.yml"
$D up -d --wait postgres
$D run --rm -T backup restore latest < chog-backup-key.txt
$D exec postgres psql -U choggames -d choggames -c 'SELECT count(*) FROM "Players"'
$D down -v      # removes the drill's volumes only
```

## Monitoring and alerts

STORY-048 (DEC-052). Nothing extra runs on the VM. You get logs, metrics, a public readiness URL and a backup heartbeat,
and an external monitor of your choice does the alerting.

**Set up alerts (once):**

1. **Backups:** create a check at a dead-man's-switch service such as healthchecks.io (free tier) with a period
   of 1 day and a grace of 2 hours. Put its ping URL in `.env` as `BACKUP_HEARTBEAT_URL`, and optionally the
   same URL plus `/fail` as `BACKUP_HEARTBEAT_FAIL_URL`. Every backup that reaches the bucket pings it. A missed
   ping (the backup failed, the service is stopped or the VM is down) alerts you after the grace period, and a
   failed scheduled backup alerts you at once. Then run `up -d --wait` again, and `run --rm backup now` to send
   the first ping.
2. **Uptime:** point an HTTP(S) uptime check (e.g. UptimeRobot, Better Stack, or a healthchecks.io HTTP check)
   at `https://<PUBLIC_HOSTNAME>/api/health/ready`, expecting `200`. It fails when nginx, the API or the
   database is down, and when the certificate is invalid. The response holds check names, statuses and durations
   only.

**Logs.** Every container logs to Docker, rotated at 5 × 10 MB.

- The API writes one JSON object per line, with `Timestamp` (UTC), `LogLevel`, `Category`, `Message`, `State`
  and `Scopes`.
- nginx's access log is JSON too: `time`, `requestId`, `remoteAddr`, `method`, `path` (no query string), `status`,
  `bytes`, `durationSeconds`, `upstreamSeconds` and `userAgent`.
- Every API response carries `X-Request-Id`, which is nginx's `requestId`. Every API log line written during that
  request has the same value as `CorrelationId` in its scopes. Ask users who report a problem for it.
- Session tokens, cookies, wallet signatures, sign-in challenges and the heartbeat URLs are never logged.

```bash
C="docker compose -f infrastructure/production/docker-compose.yml"
$C logs -f --since 1h api web                    # follow both
$C logs --no-log-prefix web | grep '"requestId":"<id>"'
$C logs --no-log-prefix api | grep '<id>'
$C logs --no-log-prefix api | grep '"LogLevel":"Error"'
$C logs backup | grep -E 'ERROR|WARNING|pinged'  # backup results and heartbeat pings
```

**Metrics.** The API serves Prometheus text at `/metrics` on its own port. nginx doesn't forward it, so read it
on the VM:

```bash
$C exec -T api bash -c "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /metrics HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && cat <&3"
```

| Metric | Labels | Meaning |
|---|---|---|
| `http_server_request_duration_seconds` (histogram) | `http_route`, `http_request_method`, `http_response_status_code` | API request durations and counts by route |
| `http_server_active_requests` | `http_request_method` | Requests in progress |
| `signalr_server_active_connections`, `signalr_server_connection_duration_seconds` | `signalr_transport`, `signalr_connection_status` | Live hub connections |
| `choggames_rooms_created_total`, `choggames_rooms_cancelled_total` | `game` | Rooms created, and closed by the host before the start |
| `choggames_matches_started_total`, `choggames_matches_ended_total` | `game`, `outcome` (`completed`, `abandoned`) | Matches |
| `choggames_rooms_open` | `game`, `status` (`waiting`, `active`) | Rooms open now (counted in the database) |
| `choggames_guest_sessions_created_total` | | New guest players |
| `choggames_wallet_sign_ins_total` | `result` (`succeeded`, `challenge_unavailable`, `invalid_signature`) | Wallet sign-ins |
| `choggames_realtime_room_subscriptions_total` | `result` (`subscribed`, `room_not_found`) | Live room subscriptions (every reconnect subscribes again) |

Counters start from zero when the API restarts. A Prometheus server could scrape `api:8080/metrics` from the
Compose network later, without any API change.

## Restarts and upgrades

- Containers restart on failure and after a reboot (`restart: unless-stopped`; enable the Docker service).
- `docker compose ... restart`, `down` and `up` keep all data: it lives in the `postgres-data`, `redis-data`
  and `backups` volumes. **Never** run `down -v` on the VM; it deletes the database.
- Upgrade: check out the new release, then `IMAGE_TAG=<release> docker compose ... up -d --build --wait`.
  Migrations run before the new API starts; if they fail, Compose stops with an error before replacing the API.
- Roll back: `IMAGE_TAG=<previous release> docker compose ... up -d --wait`. Migrations aren't rolled back
  automatically, so a release that changes the schema needs its own rollback note.
- Before production, every release passes [`docs/release-checklist.md`](release-checklist.md) on the separate
  staging VM (STORY-049, DEC-053). That includes `npm run release:verify` against staging and the rollback drill.
- Matches in progress resume after an API restart (DEC-030); every active turn gets a fresh timer.

## Checks

- `docker compose -f infrastructure/production/docker-compose.yml ps` shows every service `healthy`, and `migrate` `exited (0)`.
- `curl -fsS https://<PUBLIC_HOSTNAME>/api/games` returns the catalog; `curl -sI http://<PUBLIC_HOSTNAME>/` is a
  `301` to `https://`.
- Logs: `docker compose ... logs -f api web` (JSON lines; see "Monitoring and alerts").
- `curl -fsS https://<PUBLIC_HOSTNAME>/api/health/ready` returns `{"status":"Healthy",...}`.
- `npm run deploy:verify` (on a development machine or CI) builds and runs an isolated copy for `localhost` on
  ports 18081/18443 (without certbot). Over TLS verified against the served certificate it checks the app, the
  API, the wss hub, the HSTS and CSP headers, the HTTP redirect, the refusal of other host names, the switch to a
  stand-in issued certificate, and that a guest and the certificate survive recreating every container. It also
  checks that readiness is public while `/metrics` and liveness are not, that the request id nginx logs is the
  one the API returns, that the API logs JSON without the session token, and that metrics count the guest. Then it
  removes the copy.
