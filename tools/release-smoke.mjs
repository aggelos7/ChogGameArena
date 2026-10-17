#!/usr/bin/env node
// STORY-049 release smoke test (DEC-053): drives a running deployment (staging before every release, or a local
// stack) through its public HTTP API and the room hub the way players do, with Node built-ins only. It covers:
// readiness and the catalog; guests and their sessions; choosing a Chog (and refusing an NFT for a guest); wallet
// sign-in refusing a bad signature; a six-player room up to its limit; lobby and match rules refusing the wrong player,
// the wrong turn, a stale version and outsiders without any change; a player's forfeit; a dropped live connection
// reconnecting and catching up; a six-player and a two-player match played over match commands to the finish, with
// ranks, profile stats and achievements (STORY-058); and (STORY-057, DEC-061) a two-player Chog Race match played at the same time as the
// two-player Chog Life one, with commands meant for the other game or match refused. It creates eight guests and three
// rooms on the target and leaves them there, so it is never run against production. Wallet sign-in with a real
// wallet, choosing a real Chog NFT, the mobile UI and the deployment rollback are the manual part of
// docs/release-checklist.md.
//
//   RELEASE_BASE_URL=https://staging.example.com npm run release:verify
//
// RELEASE_BASE_URL defaults to the dev server (http://localhost:4201); RELEASE_CA_FILE trusts an extra certificate
// (PEM), e.g. a staging certificate that isn't from a public authority.
import { randomBytes, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { fileURLToPath } from 'node:url';
import { connectHub } from './hub-client.mjs';

const GAME = 'chog-life';
const RACE = 'chog-race';
const HUB_PATH = '/api/hubs/rooms';
// Guest creation is limited per client address (GuestSessions:CreatePerMinutePerClient, 10 by default).
const GUESTS = 8;

const check = (condition, message) => {
  if (!condition) throw new Error(message);
};
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Runs the release smoke test against `origin`; throws on the first failed check. */
export async function runReleaseSmoke({ origin, ca, log = console.log, matchMinutes = 25 }) {
  const base = new URL(origin);
  const secure = base.protocol === 'https:';
  const step = (message) => log(`- ${message}`);

  // One request on a fresh connection, so a dropped connection or a restarted proxy never needs a retry.
  const send = (method, path, { cookie, body } = {}) =>
    new Promise((resolve, reject) => {
      const headers = { accept: 'application/json', ...(cookie ? { cookie } : {}) };
      if (body !== undefined) headers['content-type'] = 'application/json';
      const req = (secure ? httpsRequest : httpRequest)(new URL(path, base), {
        method,
        headers,
        agent: false,
        ...(secure && ca ? { ca } : {}),
      }, (response) => {
        let text = '';
        response.setEncoding('utf8');
        response.on('data', (chunk) => (text += chunk));
        response.on('end', () => {
          let json;
          try {
            json = text ? JSON.parse(text) : undefined;
          } catch {
            json = undefined;
          }
          resolve({ status: response.statusCode, headers: response.headers, json, text });
        });
      });
      req.on('error', reject);
      req.end(body === undefined ? undefined : JSON.stringify(body));
    });

  const expectStatus = (response, status, what) =>
    check(response.status === status, `${what} returned ${response.status}, expected ${status}: ${response.text.slice(0, 300)}`);
  const expectProblem = (response, status, code, what) => {
    expectStatus(response, status, what);
    check(response.json?.code === code, `${what} returned code ${response.json?.code}, expected ${code}`);
  };

  /** A guest player: their own session cookie and, once connected, their own hub connection. */
  const createGuest = async (displayName) => {
    let created = await send('POST', '/api/guest-sessions', { body: { displayName } });
    if (created.status === 429) {
      // A run within the last minute used up this client's guest allowance; the window is one minute.
      step('guest creation is rate limited for this client; waiting a minute');
      await sleep(61_000);
      created = await send('POST', '/api/guest-sessions', { body: { displayName } });
    }
    expectStatus(created, 201, 'POST /api/guest-sessions');
    const setCookie = [created.headers['set-cookie'] ?? []].flat();
    check(setCookie.length === 1, 'a guest gets exactly one session cookie');
    check(/;\s*httponly/i.test(setCookie[0]), 'the session cookie must be HttpOnly');
    check(!secure || /;\s*secure/i.test(setCookie[0]), 'the session cookie must be Secure over HTTPS');
    check(created.json.kind === 'Guest' && created.json.displayName === displayName, 'the new player must be a guest with the chosen name');
    const cookie = setCookie[0].split(';')[0];
    return {
      name: displayName,
      cookie,
      get: (path) => send('GET', path, { cookie }),
      post: (path, body) => send('POST', path, { cookie, body }),
      put: (path, body) => send('PUT', path, { cookie, body }),
    };
  };

  const hubUrl = new URL(HUB_PATH, base).toString();
  const hubs = new Set();
  const connect = async (guest) => {
    const hub = await connectHub(hubUrl, { cookie: guest.cookie, ca });
    hubs.add(hub);
    return hub;
  };

  /**
   * Follows a room over a player's hub connection, keeping the snapshot with the highest version (DEC-028). `next`
   * waits for one newer than a version, and falls back to reading the room when no update comes, so a lost push
   * delays the test rather than failing it; the push itself is checked separately.
   */
  const follow = async (guest, code) => {
    const hub = await connect(guest);
    const watcher = { hub, latest: undefined, pushes: 0, waiters: [] };
    const offer = (room, pushed) => {
      if (room.code !== code) return;
      if (pushed) watcher.pushes++;
      if (!watcher.latest || room.version > watcher.latest.version) watcher.latest = room;
      watcher.waiters = watcher.waiters.filter((waiter) => !waiter(watcher.latest));
    };
    hub.on('RoomUpdated', (room) => offer(room, true));
    offer(await hub.invoke('SubscribeRoom', code), false);
    watcher.next = async (after, seconds = 5) => {
      if (watcher.latest.version > after) return watcher.latest;
      const pushed = await new Promise((resolve) => {
        const timer = setTimeout(() => resolve(undefined), seconds * 1000);
        watcher.waiters.push((room) => room.version > after && (clearTimeout(timer), resolve(room), true));
      });
      if (pushed) return pushed;
      const read = await guest.get(`/api/rooms/${code}`);
      expectStatus(read, 200, `GET /api/rooms/${code}`);
      offer(read.json, false);
      return watcher.latest;
    };
    return watcher;
  };

  const roomOf = async (guest, code) => {
    const response = await guest.get(`/api/rooms/${code}`);
    expectStatus(response, 200, `GET /api/rooms/${code} as ${guest.name}`);
    return response.json;
  };

  const command = (guest, room, type, payload, overrides = {}) =>
    guest.post(`/api/rooms/${room.code}/commands`, {
      actionId: randomUUID(),
      matchId: room.matchId,
      expectedVersion: room.version,
      type,
      ...(payload ? { payload } : {}),
      ...overrides,
    });

  /** Every member chooses the first ability offered to them and marks ready. */
  const readyAll = async (members, code) => {
    for (const guest of members) {
      const room = await roomOf(guest, code);
      if (room.abilities.length > 0) {
        expectStatus(await guest.put(`/api/rooms/${code}/ability`, { abilityId: room.abilities[0].id }), 200, `choosing an ability as ${guest.name}`);
      }
      expectStatus(await guest.put(`/api/rooms/${code}/ready`, { ready: true }), 200, `marking ${guest.name} ready`);
    }
  };

  /**
   * The next move in `room`, and who makes it. Chog Life: a lane choice when the player's pawn waits on a fork (or to
   * keep a previewed move), otherwise a roll. Chog Race: a pass when an attack waits for its target's reply, ending a
   * Play phase without a card, otherwise a roll.
   */
  const nextMove = (room) => {
    const turn = room.members.find((member) => member.isTurn);
    if (room.gameType === RACE) {
      const phase = room.state?.phase;
      if (phase === 'Reply') {
        return { member: room.members.find((m) => m.seat === room.state.reply.target), type: 'PASS' };
      }
      return { member: turn, type: phase === 'Play' ? 'END_TURN' : 'ROLL' };
    }
    const deciding = turn && [room.state?.choosing, room.state?.confirming].includes(turn.seat);
    return { member: turn, type: deciding ? 'CHOOSE_LANE' : 'ROLL', payload: deciding ? { lane: 'Safe' } : undefined };
  };

  /**
   * Plays an active match of either game to its end over match commands, as each player in turn (`nextMove`). The
   * watcher's hub connection says whose turn it is. `during(room, turns)` runs after every accepted command, for checks
   * along the way. Ends Completed (everyone reached the Finish, or the time cap) or Abandoned.
   */
  const playToEnd = async (watcher, players, during) => {
    const byName = new Map(players.map((guest) => [guest.name, guest]));
    const deadline = Date.now() + matchMinutes * 60_000;
    let room = watcher.latest;
    let turns = 0;
    while (room.status === 'Active') {
      check(Date.now() < deadline, `the match did not end within ${matchMinutes} minutes`);
      const move = nextMove(room);
      const player = move.member && byName.get(move.member.displayName);
      if (!player) {
        room = await watcher.next(room.version, 2);
        continue;
      }
      const response = await command(player, room, move.type, move.payload);
      if (response.status === 200) {
        turns++;
        room = await watcher.next(response.json.version - 1);
        await during?.(room, turns);
      } else if (response.status === 409 && ['stale_version', 'turn_expired', 'not_your_turn', 'time_up'].includes(response.json?.code)) {
        // The turn timer or the time cap moved the match on first; act on the new state.
        room = await watcher.next(room.version, 2);
      } else if (response.status === 403 && response.json?.code === 'not_in_match') {
        // `during` took this player out after the snapshot was read; the match has moved on without them.
        room = await watcher.next(room.version, 2);
      } else if (response.status === 429) {
        await sleep(5_000);
      } else {
        throw new Error(`${move.type} as ${player.name} returned ${response.status}: ${response.text.slice(0, 300)}`);
      }
    }
    return { room, turns };
  };

  const unlockedAchievements = async (guest) => {
    const achievements = await guest.get('/api/profile/achievements');
    expectStatus(achievements, 200, `GET /api/profile/achievements as ${guest.name}`);
    check(Array.isArray(achievements.json) && achievements.json.length === 7, 'the achievement catalog has seven entries');
    return achievements.json.filter((a) => a.unlockedAt).map((a) => a.id);
  };

  const checkResults = async (room, players, what) => {
    check(room.status === 'Completed', `${what} ended ${room.status}, expected Completed`);
    const stayed = room.members.filter((member) => !member.hasLeft);
    check(stayed.every((member) => Number.isInteger(member.rank) && member.rank >= 1), `${what}: every player still in it has a rank`);
    check(stayed.some((member) => member.rank === 1), `${what}: someone is ranked first`);
    // Players who left are ranked after everyone still playing (DEC-030).
    const lowest = Math.max(...stayed.map((member) => member.rank));
    check(room.members.filter((member) => member.hasLeft).every((member) => member.rank > lowest), `${what}: players who left rank last`);
    for (const guest of players) {
      const profile = await guest.get('/api/profile');
      expectStatus(profile, 200, `GET /api/profile as ${guest.name}`);
      const member = room.members.find((m) => m.displayName === guest.name);
      check(profile.json.gamesPlayed >= 1, `${guest.name}'s profile must count the match`);
      if (member.rank === 1) check(profile.json.wins >= 1, `${guest.name} won and must have a win`);
      // Achievements (STORY-058, DEC-063): every player has played a match, and a winner has won one.
      const unlocked = await unlockedAchievements(guest);
      check(unlocked.includes('first_match'), `${guest.name} must have unlocked First Match`);
      if (member.rank === 1) check(unlocked.includes('first_win'), `${guest.name} won and must have unlocked First Win`);
      expectStatus(await guest.get('/api/rooms/current'), 204, `GET /api/rooms/current after ${what} as ${guest.name}`);
    }
  };

  try {
    step(`target ${base.origin}`);

    step('readiness and the catalog');
    const ready = await send('GET', '/api/health/ready');
    check(ready.status === 200 && ready.json?.status === 'Healthy', `GET /api/health/ready returned ${ready.status}`);
    const games = await send('GET', '/api/games');
    expectStatus(games, 200, 'GET /api/games');
    const game = games.json.find((g) => g.id === GAME);
    check(game?.status === 'Available', `${GAME} must be Available in the catalog (set CHOG_LIFE_STATUS=Available on staging)`);
    const race = games.json.find((g) => g.id === RACE);
    check(race?.status === 'Available', `${RACE} must be Available in the catalog (set CHOG_RACE_STATUS=Available on staging)`);

    step('calls without a session are refused');
    expectStatus(await send('GET', '/api/me'), 401, 'GET /api/me without a session');
    expectStatus(await send('POST', '/api/rooms', { body: { gameType: GAME } }), 401, 'POST /api/rooms without a session');
    expectStatus(await send('GET', '/api/me', { cookie: 'chog_session=forged' }), 401, 'GET /api/me with a forged cookie');
    await connectHub(hubUrl, { ca }).then(
      (hub) => {
        hub.drop();
        throw new Error('the room hub must refuse a connection without a session');
      },
      (error) => check(/returned 401/.test(error.message), `the hub without a session: ${error.message}`),
    );

    step(`guests: ${GUESTS} players with their own sessions`);
    const run = randomBytes(2).toString('hex');
    const guests = [];
    for (let i = 1; i <= GUESTS; i++) guests.push(await createGuest(`Release ${run} ${i}`));
    for (const guest of guests) {
      const me = await guest.get('/api/me');
      check(me.status === 200 && me.json.displayName === guest.name && me.json.kind === 'Guest', `GET /api/me as ${guest.name}`);
    }
    const [host, ...others] = guests;

    step('character selection: a standard Chog; a guest cannot choose an NFT');
    const characters = await host.get('/api/characters');
    expectStatus(characters, 200, 'GET /api/characters');
    check(characters.json.nftStatus === 'NoWallet' && characters.json.nfts.length === 0, 'a guest has no NFT gallery');
    check(characters.json.standard.length >= 2, 'standard Chogs must be offered');
    const chosen = characters.json.standard.find((c) => c.id !== characters.json.selected.id);
    expectStatus(await host.put('/api/characters/selected', { kind: 'Standard', id: chosen.id }), 200, 'choosing a standard Chog');
    expectStatus(await host.put('/api/characters/selected', { kind: 'Nft', id: '1' }), 400, 'a guest choosing an NFT');
    expectStatus(await host.put('/api/characters/selected', { kind: 'Standard', id: 'no-such-chog' }), 400, 'choosing an unknown Chog');
    const afterChoice = await host.get('/api/characters');
    check(afterChoice.json.selected.kind === 'Standard' && afterChoice.json.selected.id === chosen.id, 'refused choices must not change the selection');

    step('wallet sign-in: a challenge is issued; a bad signature is refused and the guest stays a guest');
    const chain = await send('GET', '/api/wallet/chain');
    check(chain.status === 200 && Number.isInteger(chain.json.chainId), `GET /api/wallet/chain returned ${chain.status}`);
    const address = `0x${randomBytes(20).toString('hex')}`;
    const challenge = await host.post('/api/wallet/challenge', { address });
    expectStatus(challenge, 200, 'POST /api/wallet/challenge');
    check(challenge.json.message.includes(`Chain ID: ${chain.json.chainId}`), 'the sign-in message must name the chain');
    const forged = await host.post('/api/wallet/verify', { challengeId: challenge.json.challengeId, signature: `0x${'11'.repeat(65)}` });
    expectStatus(forged, 400, 'POST /api/wallet/verify with a bad signature');
    check(!forged.headers['set-cookie'], 'a refused sign-in must not change the session');
    const stillGuest = await host.get('/api/me');
    check(stillGuest.json.kind === 'Guest' && stillGuest.json.walletAddress === null, 'a refused sign-in must leave the player a guest');

    step('a six-player room: create, join up to the limit, refuse the seventh and outsiders');
    const created = await host.post('/api/rooms', { gameType: GAME });
    expectStatus(created, 201, 'POST /api/rooms');
    const code = created.json.code;
    check(created.json.status === 'Waiting' && created.json.minPlayers === 2 && created.json.capacity === 6, 'a new room waits for 2 to 6 players');
    check(created.json.members[0].character.id === chosen.id, 'the room shows the host\'s chosen Chog');
    expectProblem(await host.post('/api/rooms', { gameType: GAME }), 409, 'already_in_room', 'creating a second room');
    const table = [host, ...others.slice(0, 5)];
    const seventh = others[5];
    const outsider = others[6];
    for (const guest of table.slice(1)) expectStatus(await guest.post(`/api/rooms/${code}/join`), 200, `${guest.name} joining`);
    expectProblem(await seventh.post(`/api/rooms/${code}/join`), 409, 'room_full', 'a seventh player joining');
    expectProblem(await outsider.get(`/api/rooms/${code}`), 404, 'room_not_found', 'an outsider reading the room');
    expectProblem(await outsider.get('/api/rooms/ZZZZZZ'), 404, 'room_not_found', 'reading a room that does not exist');
    const lobby = await roomOf(host, code);
    check(lobby.members.length === 6, `the room must hold 6 players, not ${lobby.members.length}`);

    step('the lobby refuses a non-host start and a start before everyone is ready');
    expectProblem(await table[1].post(`/api/rooms/${code}/start`), 403, 'not_host', 'a non-host starting');
    expectProblem(await host.post(`/api/rooms/${code}/start`), 409, 'players_not_ready', 'starting before anyone is ready');
    check((await roomOf(host, code)).version === lobby.version, 'refused lobby actions must not change the room');

    step('live updates: members subscribe; outsiders are refused');
    const watcher = await follow(host, code);
    const second = await follow(table[1], code);
    const outsiderHub = await connect(outsider);
    await outsiderHub.invoke('SubscribeRoom', code).then(
      () => {
        throw new Error('an outsider must not subscribe to the room');
      },
      (error) => check(error.message.includes('room_not_found'), `an outsider subscribing: ${error.message}`),
    );
    await outsiderHub.close();

    step('everyone chooses an ability and marks ready; the host starts');
    await readyAll(table, code);
    const started = await host.post(`/api/rooms/${code}/start`);
    expectStatus(started, 200, 'starting the match');
    check(started.json.status === 'Active' && started.json.members.every((m) => m.seat >= 1 && m.seat <= 6), 'the match is active with 6 seats');
    const pushedStart = await second.next(started.json.version - 1);
    check(pushedStart.status === 'Active' && second.pushes > 0, 'a member must be told over the hub that the match started');

    step('match commands refuse the wrong player, a stale version, unknown commands and outsiders, and change nothing');
    let active = await watcher.next(started.json.version - 1);
    const turnName = active.members.find((m) => m.isTurn).displayName;
    const onTurn = table.find((g) => g.name === turnName);
    const offTurn = table.find((g) => g.name !== turnName);
    expectProblem(await command(offTurn, active, 'ROLL'), 409, 'not_your_turn', 'rolling out of turn');
    expectProblem(await command(onTurn, active, 'ROLL', undefined, { expectedVersion: active.version - 1 }), 409, 'stale_version', 'rolling on a stale version');
    expectProblem(await command(onTurn, active, 'TELEPORT'), 400, 'unknown_command', 'an unknown command');
    expectProblem(await command(outsider, active, 'ROLL'), 404, 'room_not_found', 'an outsider rolling');
    expectProblem(await outsider.post(`/api/rooms/${code}/join`), 409, 'room_started', 'joining a started match');
    check((await roomOf(host, code)).version === active.version, 'refused commands must not change the match');

    const leaver = table[5];
    let forfeited = false;
    let reconnected = false;
    let resumed;
    const sixPlayer = await playToEnd(watcher, table, async (room, turns) => {
      if (!forfeited && turns >= 6) {
        // A forfeit: the player leaves, keeps no seat in play and is free to start another room (DEC-025).
        step(`a player forfeits after ${turns} turns; the match continues with five`);
        expectStatus(await leaver.post(`/api/rooms/${code}/leave`), 204, 'leaving the match');
        forfeited = true;
        const after = await roomOf(host, code);
        check(after.status === 'Active' && after.members.find((m) => m.displayName === leaver.name).hasLeft, 'the match goes on without the leaver');
        expectProblem(await command(leaver, after, 'ROLL'), 403, 'not_in_match', 'a player who left rolling');
        expectStatus(await leaver.get('/api/rooms/current'), 204, 'GET /api/rooms/current after leaving');
      }
      if (resumed && !reconnected) {
        // The turn after the reconnect: it must have arrived over the new connection.
        const caughtUp = await resumed.next(room.version - 1, 10);
        check(caughtUp.version >= room.version && resumed.pushes > 0, 'the reconnected player must get later updates over the hub');
        reconnected = true;
        await resumed.hub.close();
      }
      if (!resumed && turns >= 12) {
        step(`a dropped live connection reconnects after ${turns} turns and catches up`);
        await second.hub.drop();
        const current = await table[1].get('/api/rooms/current');
        check(current.status === 200 && current.json.code === code, 'a player returning finds their match through /api/rooms/current');
        resumed = await follow(table[1], code);
        check(resumed.latest.status === 'Active' && resumed.latest.version >= room.version, 'resubscribing must return the latest snapshot');
      }
    });
    check(forfeited && reconnected, `the six-player match ended after ${sixPlayer.turns} turns, before the forfeit and reconnect checks ran`);
    step(`the six-player match finished after ${sixPlayer.turns} turns`);
    await checkResults(sixPlayer.room, table, 'the six-player match');
    expectProblem(await command(host, sixPlayer.room, 'ROLL'), 409, 'room_closed', 'rolling after the end');
    await watcher.hub.close();

    step('a two-player Chog Life match and a two-player Chog Race match, played at the same time');
    // The leaver and the refused seventh player play Chog Life; two players of the six-player match play Chog Race.
    const duo = [leaver, seventh];
    const racers = [table[1], table[2]];
    const startPair = async ([first, partner], gameType) => {
      const pair = await first.post('/api/rooms', { gameType });
      expectStatus(pair, 201, `creating a ${gameType} room`);
      expectStatus(await partner.post(`/api/rooms/${pair.json.code}/join`), 200, `joining the ${gameType} room`);
      const pairWatcher = await follow(first, pair.json.code);
      await readyAll([first, partner], pair.json.code);
      const pairStart = await first.post(`/api/rooms/${pair.json.code}/start`);
      expectStatus(pairStart, 200, `starting the ${gameType} match`);
      check(pairStart.json.gameType === gameType, `the ${gameType} room must play ${gameType}`);
      await pairWatcher.next(pairStart.json.version - 1);
      return pairWatcher;
    };
    const duoWatcher = await startPair(duo, GAME);
    const raceWatcher = await startPair(racers, RACE);
    check(raceWatcher.latest.state?.tiles?.length > 0, 'the Chog Race match shows its track');

    step('commands meant for the other game or match are refused and change nothing');
    const lifeNow = await roomOf(leaver, duoWatcher.latest.code);
    const raceNow = await roomOf(racers[0], raceWatcher.latest.code);
    const racer = racers.find((g) => g.name === raceNow.members.find((m) => m.isTurn).displayName);
    expectProblem(await command(racer, raceNow, 'CHOOSE_LANE', { lane: 'Safe' }), 400, 'unknown_command', 'a Chog Life command in Chog Race');
    expectProblem(await command(racer, raceNow, 'ROLL', undefined, { matchId: lifeNow.matchId }), 404, 'room_not_found', 'the Chog Life match id in Chog Race');
    expectProblem(await command(leaver, raceNow, 'ROLL'), 404, 'room_not_found', 'a Chog Life player rolling in Chog Race');
    check((await roomOf(leaver, lifeNow.code)).version === lifeNow.version, 'refused commands must not change the Chog Life match');
    check((await roomOf(racer, raceNow.code)).version === raceNow.version, 'refused commands must not change the Chog Race match');

    const [twoPlayer, racePlayed] = await Promise.all([playToEnd(duoWatcher, duo), playToEnd(raceWatcher, racers)]);
    step(`the two-player Chog Life match finished after ${twoPlayer.turns} turns, the Chog Race match after ${racePlayed.turns}`);
    await checkResults(twoPlayer.room, duo, 'the two-player match');
    await checkResults(racePlayed.room, racers, 'the Chog Race match');
    check(racePlayed.room.members.every((m) => m.result && Number.isInteger(m.result.tile)), 'every Chog Race player gets a result');
    check(
      racePlayed.room.members.every(
        (m) => m.result.degenScore === Math.min(100, 10 * m.result.attacksLanded) && m.result.degenScoreVersion === 1,
      ),
      'every Chog Race result carries its Degen Score, 10 per attack landed up to 100 (STORY-066)',
    );
    for (const guest of racers) {
      const profile = await guest.get('/api/profile');
      check(profile.json.gamesPlayed >= 2, `${guest.name}'s profile must count both games`);
      check((await unlockedAchievements(guest)).includes('explorer'), `${guest.name} played both games and must have unlocked Explorer`);
    }
    await duoWatcher.hub.close();
    await raceWatcher.hub.close();
    log('Release smoke test passed.');
  } finally {
    // A failed run leaves connections open; they would keep the process alive.
    await Promise.all([...hubs].map((hub) => hub.drop()));
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const origin = process.env.RELEASE_BASE_URL ?? 'http://localhost:4201';
  const ca = process.env.RELEASE_CA_FILE ? readFileSync(process.env.RELEASE_CA_FILE, 'utf8') : undefined;
  try {
    await runReleaseSmoke({ origin, ca });
  } catch (error) {
    const cause = error.cause ? ` (${error.cause.code ?? error.cause.message})` : '';
    console.error(`Release smoke test failed: ${error.message}${cause}`);
    process.exitCode = 1;
  }
}
