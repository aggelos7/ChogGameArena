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

- DEC-011: Adopt Nx for Angular workspace and frontend task orchestration; keep .NET 8 solution native, optionally wrapped by Nx run-commands. No Nx Cloud for MVP. No production topology changes.
