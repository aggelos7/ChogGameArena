# Architecture and Game Design Decision Log

Record every decision with: ID, date, status, question, decision, rationale, impact and related story IDs.

## Pending

- DEC-002: Chog Life exact movement/finish/tie-break rules — Pending game design before final balancing.
- DEC-003: Chog Race reaction windows and card resolution ordering — Pending before card engine.
- DEC-004: Guest-to-wallet profile linking and account merge behavior — Pending before linking. Sign-in binding decided in DEC-015; merging two existing histories is still open.
- DEC-005: Game branding/assets and permissions — Pending before public launch.

## Confirmed

- DEC-006: One suite-first modular monolith; add Chog Life first, then Chog Race.
- DEC-007: Local-first deployment, paid infrastructure only when needed for external testing.
- DEC-008: Shared ability pool for MVP, traits retained for future resolver.
- DEC-009: All eligible NFTs browsable; exactly one selected per match; no ability stacking.
- DEC-010: Gameplay is off-chain; fictional in-game MON is not a blockchain transfer.

- DEC-012 (2026-10-09, STORY-009 — confirmed, PR #73): Guest persistence. A guest is a `Player` row with a server-generated id; the browser holds an opaque 256-bit session token in an HttpOnly, SameSite=Lax (Secure outside local dev) cookie; the database stores only its SHA-256 hash. Sessions last 90 days, sliding on use (renewal persisted at most daily); expired or revoked sessions get a new guest. Recovery works only in the same browser while the cookie exists. Guest-to-wallet linking/merging remains DEC-004.
- DEC-013 (2026-10-09, STORY-009 — confirmed, PR #73): Same-site hosting for web and API. In staging and production the Angular app and the API must be served from the same registrable domain — e.g. `app.<domain>` + `api.<domain>`, or the API reverse-proxied at `<domain>/api`. Never a shared-suffix host such as `*.pages.dev` for one and a custom domain for the other. Rationale: guest (and later wallet) sessions use an HttpOnly `SameSite=Lax` cookie, which is the CSRF protection; a cross-site deployment would require `SameSite=None`, anti-forgery tokens and strict CORS, and is exposed to third-party-cookie blocking. Impact: E07 staging/production hosting stories, SignalR authentication. Revisit only if a cross-site deployment becomes unavoidable, together with adding anti-forgery tokens.
- DEC-001 (2026-10-10, STORY-015 — confirmed by product owner): Official Chog collection and ownership adapter.
  - **Collection:** "Chog Genesis" ERC-721 at `0xc96d31f8626c6d03fae5dcd3d61e3fb9f4a73763` on Monad Mainnet (chain 143), confirmed by the product owner on 2026-10-10.
  - **On-chain facts (read-only probe, 2026-10-10):** supports ERC-721 and Metadata, not Enumerable; token ids 1–1969 (0 and 1970 revert); about 1,000 holders.
  - **Metadata:** `tokenURI` points to `ipfs://…` JSON with 12 traits and a `.webp` image. Metadata handling is STORY-016.
  - **Ownership adapter:** a JSON-RPC client written in-house, with no indexer and no new dependency.
    - Listing scans `ownerOf` over the configured range through Multicall3 `aggregate3` (`0xcA11bde05977b3631167028862bE2a173976CA11`, verified deployed on Monad) and caches the scan for 30 s.
    - Single-token checks for match admission call the contract directly and are never cached (NFT-003, NFT-008).
    - Rejected alternatives: `eth_getLogs`, because the public RPC limits it to a 100-block range; a paid indexer API, which needs a key and an external service.
  - **Configuration:** contract, chain, range and RPC are `Nfts:Monad` settings, never user input.
- DEC-014 (2026-10-09, STORY-012 — confirmed by product owner): Wallet connection and supported network. The web app talks to wallets through the browser standards EIP-1193 and EIP-6963 directly, with no wallet SDK dependency; it covers extension and in-app-browser wallets. WalletConnect (deep links/QR to mobile wallet apps) needs a hosted project id and is deferred to a separate decision. The supported chain is server configuration (`Wallet:Chain`, public parameters only), never client input: Monad Mainnet (chain 143) by default, Monad Testnet (chain 10143) in Development. **Updated 2026-10-10 (STORY-015):** Development also uses Monad Mainnet, because Chog NFTs exist only there; sign-in is gasless and no transactions are sent. Parameters were taken from docs.monad.xyz on 2026-10-09. The NFT contract and chain for ownership checks remain DEC-001. A connected address is display state only; wallet sign-in is STORY-013.
- DEC-015 (2026-10-10, STORY-013 — confirmed by product owner): Wallet sign-in binding and verification.
  - **Sign-in binding** (the sign-in part of DEC-004):
    - A verified wallet that isn't bound to anyone upgrades the caller's current guest to a wallet player (same id, name, avatar and stats).
    - A wallet already bound to a player signs in as that player; the caller's guest is left unchanged, with no history merge.
    - A caller who is already a wallet player and signs in with a different wallet gets a separate player for it.
    - One wallet per player, enforced by a unique `Players.WalletAddress` column. Merging existing histories remains DEC-004.
  - **Session:** every sign-in replaces the session and revokes the old cookie. Sign-out revokes the session, and the browser continues as a new guest.
  - **Verification:**
    - The challenge is an EIP-4361 message built by the server from configuration (`WalletAuth:Domain`/`Uri`, the configured chain id), with a 128-bit single-use nonce and a 5-minute expiry. It's bound to the requesting session's player.
    - Each challenge is consumed atomically by the first verification attempt, successful or not.
    - The signer is recovered from the stored message with EIP-191, secp256k1 and keccak-256. High-s signatures are rejected.
  - **Wallet types:** only regular (EOA) wallets are supported. Smart-contract wallets (ERC-1271) fail safely and can be added later with an RPC check.
  - **New dependency:** BouncyCastle.Cryptography 2.7.0 (MIT), for keccak-256 and secp256k1 only; recovery is our code, tested against published vectors.
- DEC-016 (2026-10-10, STORY-016 — confirmed by product owner): NFT metadata source and stored ownership.
  - **Gateway:** metadata and images are read through IPFS gateways from configuration (`Nfts:Ipfs:Gateways`), tried in order. The default is Pinata's public gateway. On 2026-10-10 it was the only public gateway still serving the collection: ipfs.io and dweb.link answer 429, cloudflare-ipfs is gone, and the others redirect to those. A dedicated gateway can be added via secrets, with no code change. Only `ipfs://` URIs are followed and redirects are not, so the collection can't direct server requests to other hosts.
  - **Fetching:** metadata is fetched on demand, when a wallet's tokens are listed or checked, within a per-request time budget. It's stored and read again weekly; outages are retried after 5 minutes. There's no background prefetch job.
  - **Outage fallback (NFT-003):** listing may use a wallet's stored scan for up to 10 minutes while the chain can't be read. Match admission (single-token checks) always reads the chain.
  - **Metadata facts:** names are those of the shuffled metadata files, e.g. token 1 is "CHOG #1462". There are 12 traits (Background, Base, Skin, Side, Body, Mouth, Eyes, Head, Accessory, Form, Tier, Naked) and a `.webp` image of about 300 KB.
  - Image hosting and artwork rights remain DEC-005.
- DEC-011: Adopt Nx for Angular workspace and frontend task orchestration; keep .NET 8 solution native, optionally wrapped by Nx run-commands. No Nx Cloud for MVP. No production topology changes.
