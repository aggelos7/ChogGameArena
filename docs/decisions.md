# Architecture and Game Design Decision Log

Record every decision with: ID, date, status, question, decision, rationale, impact and related story IDs.

## Pending

- DEC-001: Official Chog NFT contract/chain/metadata ownership adapter — Pending verification before real RPC integration.
- DEC-002: Chog Life exact movement/finish/tie-break rules — Pending game design before final balancing.
- DEC-003: Chog Race reaction windows and card resolution ordering — Pending before card engine.
- DEC-004: Guest-to-wallet profile linking and account merge behavior — Pending before linking.
- DEC-005: Game branding/assets and permissions — Pending before public launch.

## Confirmed

- DEC-006: One suite-first modular monolith; add Chog Life first, then Chog Race.
- DEC-007: Local-first deployment, paid infrastructure only when needed for external testing.
- DEC-008: Shared ability pool for MVP, traits retained for future resolver.
- DEC-009: All eligible NFTs browsable; exactly one selected per match; no ability stacking.
- DEC-010: Gameplay is off-chain; fictional in-game MON is not a blockchain transfer.

- DEC-012 (2026-10-09, STORY-009 — confirmed, PR #73): Guest persistence. A guest is a `Player` row with a server-generated id; the browser holds an opaque 256-bit session token in an HttpOnly, SameSite=Lax (Secure outside local dev) cookie; the database stores only its SHA-256 hash. Sessions last 90 days, sliding on use (renewal persisted at most daily); expired or revoked sessions get a new guest. Recovery works only in the same browser while the cookie exists. Guest-to-wallet linking/merging remains DEC-004.
- DEC-013 (2026-10-09, STORY-009 — confirmed, PR #73): Same-site hosting for web and API. In staging and production the Angular app and the API must be served from the same registrable domain — e.g. `app.<domain>` + `api.<domain>`, or the API reverse-proxied at `<domain>/api`. Never a shared-suffix host such as `*.pages.dev` for one and a custom domain for the other. Rationale: guest (and later wallet) sessions use an HttpOnly `SameSite=Lax` cookie, which is the CSRF protection; a cross-site deployment would require `SameSite=None`, anti-forgery tokens and strict CORS, and is exposed to third-party-cookie blocking. Impact: E07 staging/production hosting stories, SignalR authentication. Revisit only if a cross-site deployment becomes unavoidable, together with adding anti-forgery tokens.
- DEC-011: Adopt Nx for Angular workspace and frontend task orchestration; keep .NET 8 solution native, optionally wrapped by Nx run-commands. No Nx Cloud for MVP. No production topology changes.
