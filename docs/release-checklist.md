# Release checklist (staging)

Every release passes this checklist on **staging** before it reaches production (STORY-049, DEC-053). The first
pass, before the public launch, covers every section. A later release runs sections 1–3 and 9–10, plus every manual
section its changes touch, and the full list at least once a quarter. Record the result in the release record at the
end. A release with a failed or skipped item doesn't go to production unless the release record says why.

## 1. Staging environment

Staging is a **separate VM** with its own host name (for example `staging.<domain>`), deployed with
[`docs/deployment.md`](deployment.md) exactly as production is. It shares nothing with production: not the
database, the `.env`, the passwords, the backup bucket or the backup key (DEC-049).

Staging `.env` differs from production's only in these values:

| Setting | Staging value | Why |
|---|---|---|
| `PUBLIC_HOSTNAME` | `staging.<domain>` | Its own DNS name and Let's Encrypt certificate |
| `POSTGRES_PASSWORD` | its own | Never production's |
| `CHOG_LIFE_STATUS` | `Available` | `CHOG_LIFE_STATUS=Available` is what the smoke test and players play |
| `COMPOSE_PROFILES` | `letsencrypt` | Backups off, or `letsencrypt,backup` with a separate bucket and key |
| `IMAGE_TAG` | the release being tested | So the previous release stays on the VM for the rollback drill |

`MONAD_RPC_URL` stays on Monad mainnet, as in production, so NFT ownership is real. Testers need a browser wallet
holding at least one Chog, and a second wallet with none.

The automated smoke test creates eight guests and two completed matches on every run. Run it on staging or a local
stack only, and **never against production**: it would add made-up players and results to real stats.

## 2. Before you start

- [ ] CI is green on the release commit: frontend, backend, `npm run deps:verify`, `npm run deploy:verify`
      (which includes the release smoke test against a production-like stack) and `npm run backup:verify`.
- [ ] The release has a tag (`IMAGE_TAG=<release>`, for example the git tag) and the previous release's tag (N-1) is
      known. Its images are still on the staging VM (`docker image ls chog-game-arena/api`).
- [ ] Schema changes since N-1: `git diff --stat <N-1>..<release> -- backend/src/ChogGames.Infrastructure/Persistence/Migrations`.
      If any, the release notes say whether N-1 still runs on the new schema (see [Rollback](#9-rollback)).

## 3. Deploy to staging and run the smoke tests

On the staging VM, with `C="docker compose -f infrastructure/production/docker-compose.yml"`:

```bash
git fetch --tags && git checkout <release>
IMAGE_TAG=<release> $C up -d --build --wait
$C ps        # every service healthy, migrate exited (0)
```

From a development machine (Node 22 and a checkout; nothing is installed on staging):

```bash
RELEASE_BASE_URL=https://staging.<domain> npm run release:verify
```

- [ ] `https://staging.<domain>/api/health/ready` returns `Healthy`; `http://` redirects to `https://`.
- [ ] `npm run release:verify` ends with `Release smoke test passed.` It covers, through nginx over TLS and wss:
  - readiness and the catalog;
  - calls without a session or with a forged cookie are refused;
  - eight guests with HttpOnly, Secure session cookies;
  - choosing a standard Chog, and a guest refused an NFT or an unknown Chog without the choice changing;
  - a wallet challenge naming the chain, and a bad signature refused while the player stays a guest;
  - a six-player room filling up, with the seventh player, outsiders and unknown codes refused;
  - a non-host start and an early start refused;
  - commands out of turn, on a stale version, unknown, from outsiders and after the end refused with nothing
    changed;
  - a player forfeiting;
  - a dropped live connection reconnecting and catching up;
  - a six-player and a two-player match played to the finish, with ranks and profile stats.
- [ ] `RELEASE_CA_FILE=<pem>` is only needed if staging runs a certificate that isn't from a public authority.

The manual sections below are the parts a script can't check: a real wallet, real NFTs, real phones and the
rollback. Use one desktop browser with the wallet extension and one phone at about 375 px wide.

## 4. Guests

- [ ] A new browser (private window) gets a guest: the profile shows a generated name and a built-in avatar.
- [ ] Change the display name and avatar and save (`Profile saved.`). They survive a reload.
- [ ] An invalid name (empty or too long) shows `Enter a valid display name.` and nothing is saved.
- [ ] Closing and reopening the browser keeps the same guest (the session cookie lasts 90 days).
- [ ] On the phone, every page fits 375 px without sideways scrolling, and buttons are easy to tap.

## 5. Wallet sign-in

- [ ] On the wrong network, the app offers to switch networks. After switching, it shows the Monad network.
- [ ] `Sign in with wallet`: the wallet shows a sign-in message for `staging.<domain>` and the chain; after signing,
      the profile shows the wallet address.
- [ ] Rejecting the signature in the wallet leaves you a guest and shows an error; nothing else changes.
- [ ] Sign out returns to a fresh guest. Signing in again with the same wallet brings back the same player
      (name, stats).
- [ ] A guest who signs in keeps their name and stats (the guest is upgraded, not replaced).

## 6. NFT selection

- [ ] Signed in with the wallet that holds a Chog, `My Chogs` lists the Chog NFTs it owns, with images and traits.
- [ ] Choose a Chog NFT and save (`Your choice is saved.`). A new room shows it as your Chog.
- [ ] Signed in with the wallet that holds none, the NFT list is empty, and the standard Chogs are still offered.
- [ ] Optional, if a spare Chog can be moved: choose it, transfer it to another wallet, then start a match.
      The start is refused for that player (`must choose again`) and they are set back to not ready.

## 7. 2–6 players

- [ ] A two-player room: the host creates it, copies the invite link, and the second player joins from the link on
      the phone. The host starts once both are ready.
- [ ] A six-player room fills (use several private windows or devices); a seventh player gets "This room is full."
- [ ] The game can't start until every player has chosen an ability and is ready.
- [ ] A player who leaves a waiting room frees their seat. The host leaving closes the room for everyone.
- [ ] During a match, `Leave and forfeit` takes that player out while the others keep playing; with one player
      left, the match ends as abandoned.

## 8. Reconnect

- [ ] Mid-match on the phone, turn on flight mode for about 10 seconds and then turn it off. The board catches up
      without a reload and shows the moves made while offline.
- [ ] Reload the page mid-match: it returns to the same match and board.
- [ ] Open the same player on a second device (same wallet): it lands in the running match.
- [ ] Restart the API mid-match (`$C restart api`): the match resumes and the current turn gets a fresh timer.

## 9. Finish

- [ ] Play a match to the end: the results show every player's rank and record, and `Play again` and
      `Find another game` work.
- [ ] Each player's profile counts the game (`Games played`), and the winner's `Wins` goes up.
- [ ] Let a turn run out once: the turn passes to the next player, and a pending lane choice takes the default
      lane.

## 10. Rollback

The drill proves the release can be undone on staging before production depends on it.

```bash
IMAGE_TAG=<N-1> $C up -d --wait          # back to the previous release, without building
$C ps                                    # all healthy
curl -fsS https://staging.<domain>/api/health/ready
IMAGE_TAG=<release> $C up -d --wait      # forward again
```

- [ ] With N-1 running, the readiness check is healthy, players from before keep their sessions, and a guest can
      create a room.
- [ ] Rolling forward again works and keeps the data.
- [ ] If the release adds migrations, `IMAGE_TAG=` alone doesn't undo them. The release notes must say either "N-1
      runs on the new schema" (checked in this drill), or how to restore from the pre-release backup
      ([Restoring](deployment.md#restoring)). Take that backup in production right before the deploy:
      `$C run --rm backup now`.

## 11. Release record

Copy this table into the release's PR or notes:

| Release | Staging deployed | `release:verify` | Manual sections passed | Rollback drill | Tester | Date |
|---|---|---|---|---|---|---|
| `<release>` | ✅/❌ | ✅/❌ | 4 5 6 7 8 9 | ✅/❌ (to `<N-1>`) | | |

Then deploy the same tag to production ([docs/deployment.md](deployment.md#restarts-and-upgrades)) and check
`https://<PUBLIC_HOSTNAME>/api/health/ready`.
