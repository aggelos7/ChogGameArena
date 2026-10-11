# CLAUDE.md

Follow `AGENTS.md`: one story per task, server-authoritative rules, no secrets in code, no unapproved dependencies,
and the definition of done. Read `docs/progress.md` first for the current state, conventions and what's next.
This file adds the working rules that have proven themselves in this repo.

## Workflow

- **Story order:** implement stories with the `implement-story` skill, in backlog order. The exception is when the backlog says otherwise; for example, STORY-063 came right after STORY-021.
- **Product questions:** ask them before coding, with a recommended option first. Record the answers as the next `DEC-0xx` in `docs/decisions.md`.
- **Git and GitHub:** commit, push, open PRs or create issues only when the user asks. After a PR, stop. The user merges and then says "proceed".
  - Commit messages start with `STORY-<nnn>:` and end with the `Co-Authored-By` line.
  - PRs target `develop`, use `.github/pull_request_template.md`, contain `Closes #<issue>`, and end with the Claude Code line.
- **New stories:** take the next free number, go in the right epic of `docs/backlog.md`, and get a GitHub issue in the same body format with labels `story`, `epic:E0x`, `priority:Px` and the epic milestone. Commit them on the current story's branch.
- **Finishing a story:**
  - Set the story to `Done` only after every acceptance criterion is verified.
  - Update the README sections for API, config and migration changes.
  - Update `docs/progress.md`.
  - Report test counts exactly.

## Commands that must be used

- **Backend Postgres tests are skipped silently** unless this is set. Always set it before claiming backend tests pass:
  ```bash
  PW=$(grep '^POSTGRES_PASSWORD=' infrastructure/.env | cut -d= -f2)
  export CHOG_TEST_POSTGRES="Host=localhost;Port=5432;Database=choggames;Username=choggames;Password=$PW"
  ```
- **Full backend check:** `dotnet build -c Release` (0 warnings), then `dotnet test -c Release --no-build`.
- **Concurrency tests:** re-run them about 8 times when touching locking.
- **Migrations:**
  - `dotnet ef migrations add <Name> --project src/ChogGames.Infrastructure --startup-project src/ChogGames.Infrastructure --output-dir Persistence/Migrations`, run from `backend/`.
  - Apply with `npm run db:migrate`.
  - Other `dotnet ef` commands need `Database__ConnectionString` in the environment.
  - Check for drift with `dotnet ef migrations has-pending-model-changes`.
- **Web:** `NX_DAEMON=false npx nx run-many --targets=lint,test,build --projects=web --output-style=static`.
- **Workspace:** `npm run test:workspace`, and `npm run deps:verify` when dependencies or Compose change.
- **Dev servers:** run `NX_DAEMON=false npm run start:all` in the background. The web app is on `:4200` and proxies `/api` to `:5248`. Restart after API changes, then smoke-test through `:4200` with curl cookie jars.
- **Installing packages:** use `npx npm@11 install …`, because npm 10.9 crashes. Use `npm ci` for plain installs.

## Code rules learned here

- **Player identity:** the player always comes from the session (`ClaimTypes.NameIdentifier`). Never accept player ids from requests, and never return them.
- **Row locks:** use `SELECT … FOR UPDATE` in a transaction, always in the order room, then players sorted by id. Re-read rows after locking with `AsNoTracking`; the caller's tracked copy may be stale.
- **Check constraints must be null-safe:** `NULL IN (...)` passes a CHECK. Write `(a IS NULL) = (b IS NULL)`-style conditions, and test every new constraint with raw-SQL inserts or updates.
- **Migrations that add NOT NULL columns** to tables with rows must backfill them before adding constraints.
- **Errors:** problem details with a stable `code` extension. Use `404` for "doesn't exist" and "not yours" alike, so nothing can be probed.
- **NFT ownership:** every check that admits a player reads the chain live; stored ownership is only an outage fallback for listing.
- **Tests:** external services are hand-written fakes inside the test class; there are no mocking libraries. Web specs drive the rendered DOM.
