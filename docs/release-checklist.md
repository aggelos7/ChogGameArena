# Release checklist

Every release passes this checklist on a **local production-like stack** before it reaches production (STORY-049,
DEC-053, revised by DEC-071: there is no staging server). The first
pass, before the public launch, covers every section. A later release runs sections 1–3 and 13–14, plus every manual
section its changes touch, and the full list at least once a quarter. Record the result in the release record at the
end. A release with a failed or skipped item doesn't go to production unless the release record says why.

## 1. Test environment (local, DEC-071)

The release is tested on your own machine, with the production Compose file itself
(`infrastructure/production/docker-compose.yml`), under its own Compose project so it never touches the dev
stack. It shares nothing with production: not the database, the `.env`, the passwords, the backup bucket or the
backup key (DEC-049). Create its settings once, in a git-ignored file outside the repository, for example
`~/chog-local-release.env`:

| Setting | Local value | Why |
|---|---|---|
| `PUBLIC_HOSTNAME` | `localhost` | Served at `https://localhost:8443` with nginx's self-signed certificate (accept the browser warning) |
| `WALLET_AUTH_DOMAIN` / `WALLET_AUTH_URI` | `localhost:8443` / `https://localhost:8443` | Wallet sign-in names the address you open |
| `HTTP_BIND` / `HTTP_BIND_V6` / `HTTP_PORT` / `HTTPS_PORT` | `127.0.0.1` / `::1` / `8080` / `8443` | Only your machine reaches it, over IPv4 and IPv6 (leaving `HTTP_BIND_V6` out would publish on every IPv6 address); no clash with other services. Without IPv6 on the machine, set `HTTP_BIND_V6` to `127.0.0.1` too |
| `POSTGRES_PASSWORD` | its own (`openssl rand -hex 24`) | Never production's |
| `CHOG_LIFE_STATUS` | `Available` | `CHOG_LIFE_STATUS=Available` is what the smoke test and testers play |
| `CHOG_RACE_STATUS` | `Available` | `CHOG_RACE_STATUS=Available`: the smoke test plays Chog Race beside Chog Life (DEC-061) |
| `CHOG_ROYALE_STATUS` | `Available` | `CHOG_ROYALE_STATUS=Available`: the smoke test plays Chog Royale beside both (DEC-094) |
| `COMPOSE_PROFILES` | empty | No certbot (no public DNS) and no backups |
| `MODERATION_OPERATOR_WALLETS` | your test operator wallet | So section 12 can review reports; production lists its real operators |
| `IMAGE_TAG` | the release being tested | So the previous release's images stay for the rollback drill |

`MONAD_RPC_URL` stays on Monad mainnet, as in production, so NFT ownership is real. Testers need a browser wallet
holding at least one Chog, and a second wallet with none.

What a local stack can't show, and where it is checked instead:
- **Real DNS and the Let's Encrypt certificate:** on the production server's first deployment, before the games are
  `Available` (section 14).
- **A real phone:** the local stack only answers `localhost`. Check phone layouts with the browser's device mode at
  375 px wide here, and play one match on a real phone on launch day (section 14).

The automated smoke test creates eight guests and four completed matches on every run. Run it on the local stack
only, and **never against production**: it would add made-up players and results to real stats.

## 2. Before you start

- [ ] CI is green on the release commit: frontend, backend, `npm run deps:verify`, `npm run deploy:verify`
      (which includes the release smoke test against a production-like stack) and `npm run backup:verify`.
- [ ] The release has a tag (`IMAGE_TAG=<release>`, for example the git tag) and the previous release's tag (N-1) is
      known. Its images are still on your machine (`docker image ls chog-game-arena/api`).
- [ ] Schema changes since N-1: `git diff --stat <N-1>..<release> -- backend/src/ChogGames.Infrastructure/Persistence/Migrations`.
      If any, the release notes say whether N-1 still runs on the new schema (see [Rollback](#13-rollback)).

## 3. Deploy locally and run the smoke tests

With `C="docker compose -p chog-release -f infrastructure/production/docker-compose.yml --env-file ~/chog-local-release.env"`:

```bash
git fetch --tags && git checkout <release>
npm run deploy:verify   # an isolated production-like stack: HTTPS, wss, the release smoke test, a restart
npm run backup:verify   # an isolated backup, encryption check and restore
IMAGE_TAG=<release> $C up -d --build --wait
$C ps                   # every service healthy, migrate exited (0)
```

- [ ] `npm run deploy:verify` and `npm run backup:verify` pass, and `https://localhost:8443/api/health/ready` returns
      `Healthy`.
- [ ] `npm run deploy:verify` ends with `Release smoke test passed.` (its `npm run release:verify` step). It covers,
      through nginx over TLS and wss:
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
  - a six-player and a two-player match played to the finish, with ranks and profile stats;
  - a two-player Chog Race match played to the finish at the same time as that two-player Chog Life one, with
    a Chog Life command, the other match's id and a player of the other match refused (DEC-061);
  - a two-player Chog Royale match played in rounds at the same time as both, every player locking a card each
    round, finishing all five rounds within five minutes with results, Explorer and a Ladder place, and a Chog
    Royale command in Chog Race, another match's id and players of other matches refused (DEC-094).
- [ ] To repeat it against the stack in this section instead, set `RELEASE_CA_FILE` to its self-signed certificate:
      `$C exec web cat /etc/nginx/tls/self-signed/fullchain.pem > /tmp/chog-local.pem`, then
      `RELEASE_BASE_URL=https://localhost:8443 RELEASE_CA_FILE=/tmp/chog-local.pem npm run release:verify`.

The manual sections below are the parts a script can't check: a real wallet, real NFTs, real phones and the
rollback. Use `https://localhost:8443` in one desktop browser with the wallet extension, and its device mode at
about 375 px wide for the phone checks.

## 4. Guests

- [ ] A new browser (private window) gets a guest: the profile shows a generated name and a built-in avatar.
- [ ] Change the display name and avatar and save (`Profile saved.`). They survive a reload.
- [ ] An invalid name (empty or too long) shows `Enter a valid display name.` and nothing is saved.
- [ ] Closing and reopening the browser keeps the same guest (the session cookie lasts 90 days).
- [ ] On the phone, every page fits 375 px without sideways scrolling, and buttons are easy to tap.

## 5. Wallet sign-in

- [ ] On the wrong network, the app offers to switch networks. After switching, it shows the Monad network.
- [ ] `Sign in with wallet`: the wallet shows a sign-in message for `localhost:8443` and the chain; after signing,
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

## 10. Chog Race

Both games share the same sessions, Chogs, rooms, profiles and live updates (DEC-061), so this section checks only
what is Chog Race's own.

- [ ] The home page lists Chog Race as playable (`CHOG_RACE_STATUS=Available`); creating a Chog Race room offers
      its four abilities.
- [ ] Play a two-player Chog Race match on the phone and the desktop: the track, rolls, cards drawn into the hand,
      playing a card, an attack answered with HODL or let through, and an ability. Both screens agree.
- [ ] While it runs, a Chog Life match on two other devices plays normally; neither board shows the other's moves.
- [ ] At the end the Results panel shows every player's place, tile and card stats, and each profile's
      `Games played` counts it with the Chog Life games.

## 11. Chog Royale

All three games share the same sessions, Chogs, rooms, profiles and live updates (DEC-061, DEC-094), so this section
checks only what is Chog Royale's own. Time the match from the start to the results.

- [ ] The home page lists Chog Royale as playable (`CHOG_ROYALE_STATUS=Available`); creating a Chog Royale room
      offers its four abilities, the same for the wallet holding a Chog as for a guest.
- [ ] Play a two-player Chog Royale match on the phone and the desktop: each round's announcement with its market
      event, choosing a card (a Rug Pull or Whale Swap at the other player) or HODL, "Chosen" on the other screen,
      an ability used once, and each reveal. Both screens agree on every balance.
- [ ] Let one round run out without choosing on one screen: that player HODLs and the match goes on.
- [ ] Reload one screen while deciding: it comes back to the same round, its chosen card and the real time left.
- [ ] While it runs, a Chog Life or Chog Race match on two other devices plays normally; no board shows another's
      moves.
- [ ] The match reaches its results within five minutes of the start, with every player's place, MON, change, MON
      lost and successful cards, and each profile's `Games played` counts it with the other games.
- [ ] With `CHOG_ROYALE_STATUS=ComingSoon` (`$C up -d --wait` after editing the file), the home page shows Chog
      Royale as Coming soon and the other games still start; then set it back.

## 12. Terms and photo reports

Uploaded photos need terms, reports and a review before the public launch (DEC-069, DEC-070).

- [ ] Every page's footer links Terms of use and Privacy; both show the contact address from
      `LEGAL_CONTACT_EMAIL` (production's must be set before the launch), and the product owner has approved their
      text.
- [ ] A new guest can't upload a photo until they tick the terms on their profile; afterwards the upload works.
- [ ] On a board's player list or the Ladder, another player's photo has a Report button with five reasons and no
      text box; your own photo has none. Reporting twice says you already reported it.
- [ ] After three different players report a photo, the others see the built-in avatar instead, and its owner sees
      "Under review" on their profile with the photo still shown to them.
- [ ] Signed in with the operator wallet, the profile links to Reported photos, which lists it with its counts.
      Dismiss shows it to everyone again; Remove (on another reported photo) gives its owner the built-in avatar.
- [ ] A player who isn't an operator opening `/moderation` sees "There's nothing here."

## 13. Rollback

The drill proves the release can be undone, on the local stack, before production depends on it.

```bash
IMAGE_TAG=<N-1> $C up -d --wait          # back to the previous release, without building
$C ps                                    # all healthy
curl -kfsS https://localhost:8443/api/health/ready
IMAGE_TAG=<release> $C up -d --wait      # forward again
```

- [ ] With N-1 running, the readiness check is healthy, players from before keep their sessions, and a guest can
      create a room.
- [ ] Rolling forward again works and keeps the data.
- [ ] If the release adds migrations, `IMAGE_TAG=` alone doesn't undo them. The release notes must say either "N-1
      runs on the new schema" (checked in this drill), or how to restore from the pre-release backup
      ([Restoring](deployment.md#restoring)). Take that backup in production right before the deploy:
      `$C run --rm backup now`.

## 14. Release record

Copy this table into the release's PR or notes:

| Release | Local stack deployed | `deploy:verify` / `backup:verify` | Manual sections passed | Rollback drill | Tester | Date |
|---|---|---|---|---|---|---|
| `<release>` | ✅/❌ | ✅/❌ | 4 5 6 7 8 9 10 11 12 | ✅/❌ (to `<N-1>`) | | |

Then deploy the same tag to production ([docs/deployment.md](deployment.md#restarts-and-upgrades)) and check:

- [ ] `https://<PUBLIC_HOSTNAME>/api/health/ready` returns `Healthy` with a valid certificate, and `http://` redirects
      to `https://`.
- [ ] On the first release only (launch day): play one short match on a real phone against the desktop, with the
      wallet that holds a Chog. It counts on the Ladder like any match.
