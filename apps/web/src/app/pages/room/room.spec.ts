import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { RoomLiveEvent, RoomLiveUpdates } from '../../rooms/room-live-updates';
import { Room, RoomAbility, RoomMember } from '../../rooms/rooms-api';
import { CATALOGUE, FORK_BOARD, RACE_BOARD, lifePawn, lifeResult } from '../../testing';
import { RoomPage } from './room';

/** Stands in for the hub connection: the spec pushes status changes and snapshots. */
class FakeLiveUpdates {
  readonly events = new Subject<RoomLiveEvent>();
  readonly watched: string[] = [];

  watch(code: string) {
    this.watched.push(code);
    return this.events;
  }
}

describe('Room page', () => {
  function member(displayName: string, overrides: Partial<RoomMember> = {}): RoomMember {
    return {
      displayName,
      avatarId: 'classic',
      collector: null,
      avatarImageUrl: null,
      avatar: { id: 'classic', name: 'Classic', color: '#5b2bd6', imageUrl: null },
      isHost: false,
      isYou: false,
      isReady: false,
      character: { kind: 'Standard', id: 'classic', name: 'Classic', color: '#5b2bd6' },
      ability: null,
      hasAbility: false,
      hasLeft: false,
      isTurn: false,
      rank: null,
      result: null,
      seat: null,
      ...overrides,
    };
  }

  const room: Room = {
    code: 'K7QF3M',
    matchId: '5f0c9a8e-3d4b-4c1a-9e2f-7a6b5c4d3e2f',
    version: 1,
    gameType: 'chog-life',
    status: 'Waiting',
    minPlayers: 2,
    capacity: 6,
    turnNumber: 0,
    turnEndsAt: null,
    turnSeconds: null,
    matchEndsAt: null,
    members: [
      member('Alice', { isHost: true }),
      member('Bob', { isYou: true, character: { kind: 'Standard', id: 'mint', name: 'Mint', color: '#1a7f5a' } }),
    ],
    state: null,
    abilities: [],
    achievements: [],
  };
  const withCarol = (version: number): Room => ({ ...room, version, members: [...room.members, member('Carol')] });

  /** Alice's view: she is the host. */
  const hostView = (overrides: Partial<Room> = {}, ready: [boolean, boolean] = [false, false]): Room => ({
    ...room,
    members: [
      member('Alice', { isHost: true, isYou: true, isReady: ready[0] }),
      member('Bob', { isReady: ready[1], character: { kind: 'Nft', id: '7', name: 'Chog #7', color: null } }),
    ],
    ...overrides,
  });

  let fixture: ComponentFixture<RoomPage>;
  let http: HttpTestingController;
  let live: FakeLiveUpdates;

  beforeEach(() => {
    live = new FakeLiveUpdates();
    TestBed.configureTestingModule({
      imports: [RoomPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RoomLiveUpdates, useValue: live },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RoomPage);
    fixture.componentRef.setInput('code', 'k7qf3m');
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const members = () =>
    Array.from(element().querySelectorAll('[data-list="members"] li .line')).map((line) =>
      Array.from(line.querySelectorAll('span'))
        .map((span) => span.textContent?.trim())
        .join(' '),
    );
  const column = (selector: string) =>
    Array.from(element().querySelectorAll(`[data-list="members"] ${selector}`)).map((node) => node.textContent?.trim());
  const maybeButton = (label: string) =>
    Array.from(element().querySelectorAll('button')).find((b) => b.textContent?.trim() === label) as HTMLButtonElement | undefined;
  const button = (label: string) =>
    Array.from(element().querySelectorAll('button')).find((b) => b.textContent?.trim() === label) as HTMLButtonElement;

  function load(body: Room = room) {
    http.expectOne('/api/rooms/K7QF3M').flush(body);
    http.expectOne('/api/games').flush(CATALOGUE);
    fixture.detectChanges();
  }

  it('shows the code, invite link and members with host and you marked', () => {
    load();

    expect(element().querySelector('h1')?.textContent).toBe('Chog Life room');
    expect(element().querySelector('[data-room-code]')?.textContent).toBe('K7QF3M');
    expect(element().querySelector('[data-invite-link]')?.textContent).toBe(`${location.origin}/join/K7QF3M`);
    expect(element().querySelector('#players-heading')?.textContent).toBe('Players 2/6');
    expect(members()).toEqual(['Alice Host', 'Bob You']);
  });

  it('shows each member’s collector badge and avatar frame, and none for members without one', () => {
    load({ ...room, members: [member('Alice', { isHost: true, collector: { id: 'patron', name: 'Patron' } }), member('Bob', { isYou: true })] });

    expect(members()).toEqual(['Alice Patron Host', 'Bob You']);
    const [alice, bob] = Array.from(element().querySelectorAll('[data-list="members"] li'));
    expect(alice.querySelector('[data-frame]')?.getAttribute('data-frame')).toBe('patron');
    expect(bob.querySelector('[data-frame]')).toBeNull();
  });

  it('draws each member’s profile avatar, as their header does, and names their match Chog (STORY-085)', () => {
    load({
      ...room,
      members: [
        member('Alice', {
          isHost: true,
          avatar: { id: 'ocean', name: 'Ocean', color: '#1d4ed8', imageUrl: null },
          character: { kind: 'Nft', id: '1015', name: 'Chog #1015', color: null },
        }),
        member('Bob', { isYou: true, avatar: { id: 'classic', name: 'Classic', color: '#5b2bd6', imageUrl: '/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10' } }),
      ],
    });

    const [alice, bob] = Array.from(element().querySelectorAll('[data-list="members"] li'));
    const aliceBadge = alice.querySelector('app-avatar-badge .badge') as HTMLElement;
    expect(aliceBadge.style.background).toBe('rgb(29, 78, 216)');
    expect(aliceBadge.textContent?.trim()).toBe('A');
    expect(alice.textContent).toContain('Chog #1015');
    expect(bob.querySelector('app-avatar-badge img')?.getAttribute('src')).toBe('/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10');
  });

  it('copies the invite link', async () => {
    load();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    button('Copy invite link').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith(`${location.origin}/join/K7QF3M`);
    expect(element().querySelector('.copy-status')?.textContent?.trim()).toBe('Link copied.');
  });

  /** The server pushes the player's snapshot after a change. */
  function changed(body: Room) {
    live.events.next({ type: 'snapshot', room: body });
    fixture.detectChanges();
  }

  const liveStatus = () => element().querySelector('[data-live]');

  it('follows the room live and shows players who joined without a refresh', () => {
    load();
    expect(live.watched).toEqual(['K7QF3M']);
    expect(liveStatus()?.getAttribute('data-live')).toBe('connecting');
    expect(maybeButton('Refresh')).toBeUndefined();

    changed(withCarol(2));

    expect(members()).toEqual(['Alice Host', 'Bob You', 'Carol']);
  });

  it('shows the snapshot it gets each time it goes live, without fetching', () => {
    load();

    live.events.next({ type: 'status', status: 'live' });
    changed(withCarol(4));

    expect(liveStatus()?.textContent?.trim()).toBe('Players appear and get ready here as it happens.');
    expect(members()).toEqual(['Alice Host', 'Bob You', 'Carol']);

    live.events.next({ type: 'status', status: 'connecting' });
    fixture.detectChanges();
    expect(liveStatus()?.textContent?.trim()).toBe('Connecting for live updates…');
  });

  it('keeps the newest version when snapshots arrive out of order', () => {
    load();

    changed(withCarol(3));
    changed({ ...room, version: 2 });
    expect(members()).toEqual(['Alice Host', 'Bob You', 'Carol']);

    // A missed version doesn't matter: every snapshot is complete.
    changed({ ...room, version: 7 });
    expect(members()).toEqual(['Alice Host', 'Bob You']);
  });

  it('ignores an answer older than a snapshot already pushed', () => {
    load();

    button('I’m ready').click();
    changed({ ...withCarol(3), members: [room.members[0], { ...room.members[1], isReady: true }, member('Carol')] });
    http.expectOne('/api/rooms/K7QF3M/ready').flush({ ...room, version: 2, members: [room.members[0], { ...room.members[1], isReady: true }] });
    fixture.detectChanges();

    expect(members()).toEqual(['Alice Host', 'Bob You', 'Carol']);
    expect(column('[data-readiness]')).toEqual(['Not ready', 'Ready', 'Not ready']);
  });

  it('ignores a snapshot of another match', () => {
    load();

    changed({ ...withCarol(9), matchId: '00000000-0000-4000-8000-000000000000' });

    expect(members()).toEqual(['Alice Host', 'Bob You']);
  });

  it('offers Refresh while live updates are off, which reloads and reconnects', () => {
    load();

    live.events.next({ type: 'status', status: 'offline' });
    fixture.detectChanges();
    expect(liveStatus()?.textContent?.trim()).toBe('Live updates are off; still trying to reconnect. Refresh to see the latest now.');

    button('Refresh').click();
    http.expectOne('/api/rooms/K7QF3M').flush(withCarol(2));
    fixture.detectChanges();

    expect(members()).toEqual(['Alice Host', 'Bob You', 'Carol']);
    expect(live.watched).toEqual(['K7QF3M', 'K7QF3M']);
  });

  it('stops following the room when the page closes', () => {
    load();
    expect(live.events.observed).toBe(true);

    fixture.destroy();

    expect(live.events.observed).toBe(false);
  });

  it('tells a non-member the room is not theirs to see', () => {
    http.expectOne('/api/rooms/K7QF3M').flush({ code: 'room_not_found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(element().querySelector('[data-error="room"]')?.textContent).toContain('you’re not in it');
    expect(element().querySelector('[data-list="members"]')).toBeNull();
  });

  it('offers a retry when the room cannot be loaded', () => {
    http.expectOne('/api/rooms/K7QF3M').flush('down', { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    button('Try again').click();
    load();

    expect(members()).toHaveLength(2);
  });

  it('shows each player’s Chog and who is ready, and no ability in a game without abilities', () => {
    load(hostView({}, [false, true]));

    expect(column('[data-chog]')).toEqual(['Classic', 'Chog #7']);
    expect(column('[data-ability]')).toEqual([]);
    expect(column('[data-readiness]')).toEqual(['Not ready', 'Ready']);
    expect(element().querySelector('[data-list="abilities"]')).toBeNull();
    expect(button('I’m ready').disabled).toBe(false);
  });

  const boost: RoomAbility = {
    id: 'reward_boost',
    name: 'Reward Boost',
    description: 'Raise one reward.',
    target: 'None',
    maxUses: 1,
    usesLeft: null,
  };
  const reroll: RoomAbility = {
    id: 'event_reroll',
    name: 'Lucky Reroll',
    description: 'Reroll one event.',
    target: 'None',
    maxUses: 1,
    usesLeft: null,
  };

  /** Bob's view of a room with abilities; Alice's choice, if any, stays hidden from him. */
  const withAbilities = (bob: Partial<RoomMember> = {}, aliceChose = false): Room => ({
    ...room,
    abilities: [boost, reroll],
    members: [member('Alice', { isHost: true, hasAbility: aliceChose }), { ...room.members[1], ...bob }],
  });
  const abilityButtons = () =>
    Array.from(element().querySelectorAll('[data-list="abilities"] button')) as HTMLButtonElement[];

  it('offers the game’s abilities and needs one before ready', () => {
    load(withAbilities());

    expect(abilityButtons().map((b) => b.querySelector('.ability-name')?.textContent)).toEqual(['Reward Boost', 'Lucky Reroll']);
    expect(abilityButtons().map((b) => b.getAttribute('aria-pressed'))).toEqual(['false', 'false']);
    expect(column('[data-ability]')).toEqual(['Ability not chosen yet', 'Ability not chosen yet']);
    expect(button('I’m ready').disabled).toBe(true);
    expect(element().querySelector('[data-ability-hint]')?.textContent?.trim()).toBe('Choose an ability to get ready.');
  });

  it('chooses an ability, then allows ready', () => {
    load(withAbilities());

    abilityButtons()[1].click();
    const choose = http.expectOne('/api/rooms/K7QF3M/ability');
    expect(choose.request.method).toBe('PUT');
    expect(choose.request.body).toEqual({ abilityId: 'event_reroll' });
    choose.flush({ ...withAbilities({ ability: reroll, hasAbility: true }), version: 2 });
    fixture.detectChanges();

    expect(abilityButtons().map((b) => b.getAttribute('aria-pressed'))).toEqual(['false', 'true']);
    expect(column('[data-ability]')).toEqual(['Ability not chosen yet', 'Lucky Reroll']);
    expect(button('I’m ready').disabled).toBe(false);
    expect(element().querySelector('[data-ability-hint]')).toBeNull();
  });

  it('does not send the ability already chosen again', () => {
    load(withAbilities({ ability: boost, hasAbility: true }));

    abilityButtons()[0].click();

    http.expectNone('/api/rooms/K7QF3M/ability');
  });

  it('shows only that another player has chosen, not what', () => {
    load(withAbilities({}, true));

    expect(column('[data-ability]')).toEqual(['Ability chosen', 'Ability not chosen yet']);
    expect(element().textContent).not.toContain('Reward Boost ·');
  });

  it('shows everyone’s locked ability once the game has started', () => {
    load(withAbilities({ ability: reroll, hasAbility: true }, true));

    changed({
      ...room,
      version: 5,
      status: 'Active',
      abilities: [],
      members: [
        member('Alice', { isHost: true, isReady: true, ability: boost, hasAbility: true }),
        { ...room.members[1], isReady: true, ability: reroll, hasAbility: true },
      ],
    });

    expect(column('[data-ability]')).toEqual(['Reward Boost', 'Lucky Reroll']);
    expect(element().querySelector('[data-list="abilities"]')).toBeNull();
    expect(element().querySelector('[data-locked-ability]')?.textContent).toBe('Lucky Reroll');
  });

  it('shows how often the locked ability can still be used', () => {
    const started = (usesLeft: number): Room => ({
      ...room,
      version: 10 - usesLeft,
      status: 'Active',
      abilities: [],
      members: [
        member('Alice', { isHost: true, isReady: true, ability: { ...boost, usesLeft: 1 }, hasAbility: true, seat: 1 }),
        { ...room.members[1], isReady: true, ability: { ...reroll, maxUses: 2, usesLeft }, hasAbility: true, seat: 2 },
      ],
    });
    load(withAbilities({ ability: reroll, hasAbility: true }, true));

    changed(started(2));
    expect(element().querySelector('[data-uses-left]')?.textContent).toBe('2 uses left.');
    changed(started(1));
    expect(element().querySelector('[data-uses-left]')?.textContent).toBe('1 use left.');
    changed(started(0));
    expect(element().querySelector('[data-uses-left]')?.textContent).toBe('Already used.');
  });

  it('explains a start refused for an ability no longer offered and shows the lobby as it is now', () => {
    const view = (bobReady: boolean, bobChose: boolean): Room => ({
      ...hostView({ abilities: [boost] }, [true, bobReady]),
      members: [
        member('Alice', { isHost: true, isYou: true, isReady: true, ability: boost, hasAbility: true }),
        member('Bob', { isReady: bobReady, hasAbility: bobChose }),
      ],
    });
    load(view(true, true));

    button('Start game').click();
    http.expectOne('/api/rooms/K7QF3M/start').flush({ code: 'ability_unavailable' }, { status: 409, statusText: 'Conflict' });
    http.expectOne('/api/rooms/K7QF3M').flush({ ...view(false, false), version: 3 });
    fixture.detectChanges();

    expect(element().querySelector('[data-error="action"]')?.textContent).toBe(
      'A player’s chosen ability is no longer available and they must choose again.',
    );
    expect(column('[data-ability]')).toEqual(['Reward Boost', 'Ability not chosen yet']);
    expect(button('Start game').disabled).toBe(true);
  });

  it('marks the player ready and back', () => {
    load();

    button('I’m ready').click();
    const ready = http.expectOne('/api/rooms/K7QF3M/ready');
    expect(ready.request.method).toBe('PUT');
    expect(ready.request.body).toEqual({ ready: true });
    ready.flush({ ...room, members: [room.members[0], { ...room.members[1], isReady: true }] });
    fixture.detectChanges();

    expect(column('[data-readiness]')).toEqual(['Not ready', 'Ready']);
    button('Not ready').click();
    const notReady = http.expectOne('/api/rooms/K7QF3M/ready');
    expect(notReady.request.body).toEqual({ ready: false });
    notReady.flush(room);
  });

  it('shows only the host a start button, enabled once enough players are all ready', () => {
    load();
    expect(maybeButton('Start game')).toBeUndefined();
    expect(element().textContent).toContain('The host starts the game when everyone is ready.');

    changed(hostView({}, [true, false]));
    expect(button('Start game').disabled).toBe(true);
    expect(element().querySelector('[data-start-hint]')?.textContent?.trim()).toBe(
      'Starts with 2–6 players, all ready, including you.',
    );

    changed(hostView({}, [true, true]));
    expect(button('Start game').disabled).toBe(false);
  });

  it('keeps start disabled for a host alone', () => {
    load(hostView({ members: [member('Alice', { isHost: true, isYou: true, isReady: true })] }));

    expect(button('Start game').disabled).toBe(true);
  });

  it('starts the game and hides the lobby controls', () => {
    load(hostView({}, [true, true]));

    button('Start game').click();
    const start = http.expectOne('/api/rooms/K7QF3M/start');
    expect(start.request.method).toBe('POST');
    start.flush(hostView({ status: 'Active' }, [true, true]));
    fixture.detectChanges();

    expect(element().querySelector('[data-started]')?.textContent).toBe('The game has started.');
    expect(maybeButton('Start game')).toBeUndefined();
    expect(maybeButton('Not ready')).toBeUndefined();
  });

  it('explains a refused start and shows the lobby as it is now', () => {
    load(hostView({}, [true, true]));

    button('Start game').click();
    http
      .expectOne('/api/rooms/K7QF3M/start')
      .flush({ code: 'character_unavailable' }, { status: 409, statusText: 'Conflict' });
    http.expectOne('/api/rooms/K7QF3M').flush(hostView({}, [true, false]));
    fixture.detectChanges();

    expect(element().querySelector('[data-error="action"]')?.textContent).toBe(
      'A player no longer owns their chosen Chog and must choose again.',
    );
    expect(column('[data-readiness]')).toEqual(['Ready', 'Not ready']);
    expect(button('Start game').disabled).toBe(true);
  });

  it('explains when ownership cannot be checked without reloading', () => {
    load(hostView({}, [true, true]));

    button('Start game').click();
    http
      .expectOne('/api/rooms/K7QF3M/start')
      .flush({ code: 'ownership_unavailable' }, { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    expect(element().querySelector('[data-error="action"]')?.textContent).toContain('can’t be checked right now');
    expect(button('Start game').disabled).toBe(false);
  });

  it('links to the Chog gallery to change Chog', () => {
    load();

    const link = element().querySelector('.lobby a');
    expect(link?.getAttribute('href')).toBe('/chogs');
  });

  describe('leaving', () => {
    let navigate: ReturnType<typeof vi.spyOn>;

    beforeEach(() => {
      navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    });

    it('lets a member leave at once and returns to the game', () => {
      load();

      button('Leave room').click();
      const leave = http.expectOne('/api/rooms/K7QF3M/leave');
      expect(leave.request.method).toBe('POST');
      leave.flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledWith(['/games', 'chog-life']);
    });

    it('asks the host to confirm that leaving closes the room', () => {
      load(hostView());

      button('Leave room').click();
      fixture.detectChanges();
      http.expectNone('/api/rooms/K7QF3M/leave');
      expect(element().querySelector('[data-leave] [role="alert"]')?.textContent).toContain('closes this room for everyone');

      button('Stay').click();
      fixture.detectChanges();
      expect(maybeButton('Leave and close room')).toBeUndefined();

      button('Leave room').click();
      fixture.detectChanges();
      button('Leave and close room').click();
      http.expectOne('/api/rooms/K7QF3M/leave').flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledWith(['/games', 'chog-life']);
    });

    it('explains a refused leave and shows the room as it is now', () => {
      load();

      button('Leave room').click();
      http.expectOne('/api/rooms/K7QF3M/leave').flush({ code: 'room_started' }, { status: 409, statusText: 'Conflict' });
      http.expectOne('/api/rooms/K7QF3M').flush({ ...room, status: 'Active' });
      fixture.detectChanges();

      expect(navigate).not.toHaveBeenCalled();
      expect(element().querySelector('[data-started]')?.textContent).toBe('The game has started.');
      expect(maybeButton('Leave room')).toBeUndefined();
    });

    it('asks before forfeiting a started game, then returns to the game', () => {
      load({ ...room, status: 'Active' });

      expect(element().querySelector('.match')?.textContent).toContain('You’re playing as Mint for this game.');
      expect(column('[data-readiness]')).toEqual([]);
      button('Leave game').click();
      fixture.detectChanges();
      http.expectNone('/api/rooms/K7QF3M/leave');
      expect(element().querySelector('[data-leave] [role="alert"]')?.textContent).toContain('forfeits this game');

      button('Stay').click();
      fixture.detectChanges();
      expect(maybeButton('Leave and forfeit')).toBeUndefined();

      button('Leave game').click();
      fixture.detectChanges();
      button('Leave and forfeit').click();
      const leave = http.expectOne('/api/rooms/K7QF3M/leave');
      expect(leave.request.method).toBe('POST');
      leave.flush(null, { status: 204, statusText: 'No Content' });

      expect(navigate).toHaveBeenCalledWith(['/games', 'chog-life']);
    });

    it('shows who left a started game', () => {
      load({ ...room, status: 'Active', members: [member('Alice', { isHost: true, hasLeft: true }), member('Bob', { isYou: true })] });

      expect(members()).toEqual(['Alice Host Left', 'Bob You']);
      expect(element().querySelector('[data-started]')?.textContent).toBe('The game has started.');
      expect(maybeButton('Leave game')).toBeDefined();
    });

    it('shows the Chog Life board with every pawn once the game has started, and follows it live', () => {
      const started: Room = {
        ...room,
        status: 'Active',
        state: FORK_BOARD,
        members: [member('Alice', { isHost: true, seat: 1, isTurn: true }), member('Bob', { isYou: true, seat: 2 })],
      };
      load(started);

      const board = () => element().querySelector('.match [data-board]');
      expect(board()?.querySelectorAll('[data-tile]').length).toBe(FORK_BOARD.tiles.length);
      expect(Array.from(board()?.querySelectorAll('[data-tile="0"] [data-pawn]') ?? []).map((p) => p.textContent?.trim())).toEqual(['1', '2']);
      expect(board()?.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s Alice’s turn.');

      live.events.next({
        type: 'snapshot',
        room: {
          ...started,
          version: 2,
          members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, isTurn: true })],
        },
      });
      fixture.detectChanges();

      expect(board()?.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s your turn.');
    });

    it('hides the invite and the lobby list while the board shows the players, but not a dropped live connection', () => {
      load({ ...room, state: FORK_BOARD });
      expect(element().querySelector('[data-invite-link]')).not.toBeNull();
      expect(element().querySelector('[data-list="members"]')).not.toBeNull();

      const started: Room = {
        ...room,
        version: 2,
        status: 'Active',
        state: FORK_BOARD,
        turnEndsAt: new Date(Date.now() + 20_000).toISOString(),
        turnSeconds: 25,
        members: [member('Alice', { isHost: true, seat: 1, isTurn: true }), member('Bob', { isYou: true, seat: 2 })],
      };
      changed(started);
      live.events.next({ type: 'status', status: 'live' });
      fixture.detectChanges();
      // The title and your Chog go beside the board, and the turn timer over it.
      expect(Array.from(element().querySelectorAll('h1')).map((h) => h.closest('.side') !== null)).toEqual([true]);
      expect(element().querySelector('[data-board] .side .game-header')?.textContent).toContain('You’re playing as');
      expect(element().querySelector('[data-board] .stage app-turn-timer')).not.toBeNull();
      expect(element().querySelector('[data-invite-link]')).toBeNull();
      expect(element().querySelector('[data-list="members"]')).toBeNull();
      expect(element().querySelector('.match')?.classList).toContain('game-wide');
      // The banner stays for screen readers, and the live status says nothing while all is well.
      expect(element().querySelector('[data-started]')?.classList).toContain('visually-hidden');
      expect(liveStatus()?.textContent?.trim()).toBe('');

      live.events.next({ type: 'status', status: 'offline' });
      fixture.detectChanges();
      expect(liveStatus()?.textContent).toContain('Live updates are off');
      expect(maybeButton('Refresh')).toBeDefined();

      // A player who left has no board, so the lobby list shows who is still playing.
      changed({ ...started, version: 3, members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, hasLeft: true })] });
      expect(element().querySelector('[data-list="members"]')).not.toBeNull();
      expect(element().querySelector('[data-invite-link]')).toBeNull();
    });

    it('rolls on your turn and shows the room as the server left it', () => {
      const started: Room = {
        ...room,
        version: 4,
        status: 'Active',
        state: FORK_BOARD,
        members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, isTurn: true })],
      };
      load(started);

      element().querySelector<HTMLButtonElement>('[data-roll]')?.click();
      const roll = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(roll.request.method).toBe('POST');
      expect(roll.request.body).toEqual({
        actionId: expect.stringMatching(/^[0-9a-f-]{36}$/),
        matchId: room.matchId,
        expectedVersion: 4,
        type: 'ROLL',
        payload: {},
      });
      fixture.detectChanges();
      expect(element().querySelector<HTMLButtonElement>('[data-roll]')?.disabled).toBe(true);
      roll.flush({ actionId: roll.request.body.actionId, type: 'ROLL', version: 5, currentVersion: 5, replayed: false });

      http.expectOne('/api/rooms/K7QF3M').flush({
        ...started,
        version: 5,
        state: { ...FORK_BOARD, pawns: [lifePawn(1, 0, 100), lifePawn(2, 1, 100)], lastMove: { seat: 2, roll: 1, from: 0, to: 1, path: [1] } },
        members: [member('Alice', { isHost: true, seat: 1, isTurn: true }), member('Bob', { isYou: true, seat: 2 })],
      });
      fixture.detectChanges();

      expect(element().querySelector('[data-last-move]')?.textContent?.trim()).toBe('You rolled 1 and moved to tile 1 (+20 MON).');
      expect(element().querySelector('[data-roll]')).toBeNull();
    });

    it('sends the route you choose at a fork and shows the room as the server left it', () => {
      const atFork = { ...FORK_BOARD, pawns: [lifePawn(1, 0, 100), lifePawn(2, 2, 100)], choosing: 2, defaultLane: 'Safe' as const };
      const started: Room = {
        ...room,
        version: 6,
        status: 'Active',
        state: { ...atFork, lastMove: { seat: 2, roll: 2, from: 0, to: 2, path: [1, 2] } },
        members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, isTurn: true })],
      };
      load(started);
      expect(element().querySelector('[data-roll]')).toBeNull();

      element().querySelector<HTMLButtonElement>('[data-route="Degen"]')?.click();
      const choose = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(choose.request.body).toEqual({
        actionId: expect.stringMatching(/^[0-9a-f-]{36}$/),
        matchId: room.matchId,
        expectedVersion: 6,
        type: 'CHOOSE_LANE',
        payload: { lane: 'Degen' },
      });
      fixture.detectChanges();
      expect(element().querySelector<HTMLButtonElement>('[data-route="Safe"]')?.disabled).toBe(true);
      choose.flush({ actionId: choose.request.body.actionId, type: 'CHOOSE_LANE', version: 7, currentVersion: 7, replayed: false });

      http.expectOne('/api/rooms/K7QF3M').flush({
        ...started,
        version: 7,
        state: {
          ...FORK_BOARD,
          pawns: [lifePawn(1, 0, 100), lifePawn(2, 2, 100, { lane: 'Degen' })],
          lastMove: { seat: 2, roll: 2, from: 0, to: 2, path: [1, 2], lane: 'Degen' },
          defaultLane: 'Safe',
        },
        members: [member('Alice', { isHost: true, seat: 1, isTurn: true }), member('Bob', { isYou: true, seat: 2 })],
      });
      fixture.detectChanges();

      expect(element().querySelector('[data-routes]')).toBeNull();
      expect(element().querySelector('[data-last-move]')?.textContent?.trim()).toBe(
        'You rolled 2 and moved to tile 2 (Safe or Degen?). You chose the degen route.',
      );
    });

    it('uses your ability on your turn and shows the room as the server left it (STORY-042)', () => {
      const boost = { id: 'reward_boost', name: 'Reward Boost', description: 'Boost a reward.', target: 'None' as const, maxUses: 1 };
      const started: Room = {
        ...room,
        version: 8,
        status: 'Active',
        state: FORK_BOARD,
        members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, isTurn: true, ability: { ...boost, usesLeft: 1 } })],
      };
      load(started);

      element().querySelector<HTMLButtonElement>('[data-use-ability]')?.click();
      const use = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(use.request.body).toEqual({
        actionId: expect.stringMatching(/^[0-9a-f-]{36}$/),
        matchId: room.matchId,
        expectedVersion: 8,
        type: 'USE_ABILITY',
        payload: { abilityId: 'reward_boost' },
      });
      use.flush({ actionId: use.request.body.actionId, type: 'USE_ABILITY', version: 9, currentVersion: 9, replayed: false });

      http.expectOne('/api/rooms/K7QF3M').flush({
        ...started,
        version: 9,
        state: { ...FORK_BOARD, pawns: [lifePawn(1, 0), lifePawn(2, 0, 100, { armed: 'reward_boost' })] },
        members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, isTurn: true, ability: { ...boost, usesLeft: 0 } })],
      });
      fixture.detectChanges();

      expect(element().querySelector('[data-use-ability]')).toBeNull();
      expect(element().querySelector('[data-armed]')?.textContent?.trim()).toBe('Reward Boost armed');
      expect(element().querySelector('[data-roll]')).not.toBeNull();
    });

    it('shows whose turn it is and how much of it is left (DEC-045)', () => {
      const endsAt = new Date(Date.now() + 20_000).toISOString();
      load({
        ...room,
        status: 'Active',
        turnEndsAt: endsAt,
        turnSeconds: 25,
        state: FORK_BOARD,
        members: [member('Alice', { isHost: true, seat: 1, isTurn: true }), member('Bob', { isYou: true, seat: 2 })],
      });

      const timer = element().querySelector('[data-turn-timer]');
      expect(timer?.querySelector('.who')?.textContent).toBe('Alice’s turn');
      expect(timer?.querySelector('[data-turn-left]')?.textContent).toMatch(/^(19|20)s left$/);
      expect(timer?.querySelector<HTMLElement>('[data-turn-bar]')?.style.width).toMatch(/%$/);
    });

    it('shows no turn timer before the match starts', () => {
      load(room);

      expect(element().querySelector('[data-turn-timer]')).toBeNull();
    });

    it('explains a refused roll and shows the room as it is now', () => {
      const started: Room = {
        ...room,
        status: 'Active',
        state: FORK_BOARD,
        members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, isTurn: true })],
      };
      load(started);

      element().querySelector<HTMLButtonElement>('[data-roll]')?.click();
      http.expectOne('/api/rooms/K7QF3M/commands').flush({ code: 'turn_expired' }, { status: 409, statusText: 'Conflict' });
      http.expectOne('/api/rooms/K7QF3M').flush({ ...started, version: 2 });
      fixture.detectChanges();

      expect(element().querySelector('[data-error="action"]')?.textContent).toBe('Your turn has run out.');
    });

    it('counts down to the time cap and explains a command refused because time is up', () => {
      const started: Room = {
        ...room,
        status: 'Active',
        state: FORK_BOARD,
        matchEndsAt: new Date(Date.now() + 754_000).toISOString(),
        members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, isTurn: true })],
      };
      load(started);

      expect(element().querySelector('[data-clock]')?.textContent?.trim()).toBe('Time left 12:34');

      element().querySelector<HTMLButtonElement>('[data-roll]')?.click();
      http.expectOne('/api/rooms/K7QF3M/commands').flush({ code: 'time_up' }, { status: 409, statusText: 'Conflict' });
      http.expectOne('/api/rooms/K7QF3M').flush({ ...started, version: 2 });
      fixture.detectChanges();

      expect(element().querySelector('[data-error="action"]')?.textContent).toBe('Time’s up: the game is ending.');
    });

    it('keeps the final board under the result once the game is over', () => {
      load({
        ...room,
        status: 'Completed',
        matchEndsAt: new Date(Date.now() - 1000).toISOString(),
        state: {
          ...FORK_BOARD,
          pawns: [lifePawn(1, 9, 100, { bonuses: 100, place: 1 }), lifePawn(2, 6, 150)],
          ended: 'TimeUp',
        },
        members: [member('Alice', { isHost: true, seat: 1, rank: 1 }), member('Bob', { isYou: true, seat: 2, rank: 2 })],
      });

      expect(element().querySelector('[data-result]')?.textContent).toContain('you finished 2nd');
      const final = element().querySelector('[data-final-board]');
      expect(final?.querySelector('h2')?.textContent).toBe('Final board');
      expect(final?.querySelector('[data-ending]')?.textContent).toBe('Time’s up! Players are ranked by their final portfolio.');
      expect(Array.from(final?.querySelectorAll('[data-portfolio]') ?? []).map((p) => p.textContent)).toEqual(['200 MON', '150 MON']);
      expect(final?.querySelector('[data-clock]')).toBeNull();
      expect(final?.querySelector('[data-roll]')).toBeNull();
      expect(maybeButton('Leave game')).toBeUndefined();
    });

    it('shows the results the server stored above the final board once the game is over', () => {
      load({
        ...room,
        status: 'Completed',
        state: { ...FORK_BOARD, pawns: [lifePawn(1, 9, 150, { bonuses: 100, place: 1 }), lifePawn(2, 6, 120)], ended: 'Finish' },
        members: [
          member('Alice', { isHost: true, seat: 1, rank: 1, result: lifeResult(250, { place: 1, degenScore: 80 }) }),
          member('Bob', { isYou: true, seat: 2, rank: 2, result: lifeResult(120) }),
        ],
      });

      const results = element().querySelector('[data-results]');
      expect(results?.querySelector('[data-winner]')?.textContent).toBe('Alice wins with 250 MON!');
      expect(Array.from(results?.querySelectorAll('[data-stat="degen-score"]') ?? []).map((s) => s.textContent)).toEqual(['80/100', '0/100']);
      const final = element().querySelector('[data-final-board]');
      expect(final && results?.compareDocumentPosition(final)).toBe(Node.DOCUMENT_POSITION_FOLLOWING);
    });

    it('shows no results for a completed game that has none', () => {
      load({
        ...room,
        status: 'Completed',
        state: { ...FORK_BOARD, ended: 'TimeUp' },
        members: [member('Alice', { isHost: true, seat: 1, rank: 1 }), member('Bob', { isYou: true, seat: 2, rank: 1 })],
      });

      expect(element().querySelector('[data-results]')).toBeNull();
      expect(element().querySelector('[data-final-board]')).not.toBeNull();
    });

    it('shows no board while waiting, for another game or for a state it cannot read', () => {
      load({ ...room, state: FORK_BOARD });
      expect(element().querySelector('[data-board]')).toBeNull();

      live.events.next({ type: 'snapshot', room: { ...room, version: 2, status: 'Active', gameType: 'chog-race', state: FORK_BOARD } });
      fixture.detectChanges();
      expect(element().querySelector('[data-board]')).toBeNull();

      live.events.next({ type: 'snapshot', room: { ...room, version: 3, status: 'Active', state: { tiles: 'nope' } } });
      fixture.detectChanges();
      expect(element().querySelector('[data-board]')).toBeNull();
      expect(element().querySelector('.match')?.textContent).toContain('You’re playing as Mint for this game.');
    });

    it('shows the Chog Race board with all six pawns, its title beside it, once the game has started (STORY-050)', () => {
      const names = ['Alice', 'Bob', 'Carol', 'Dave', 'Erin', 'Frank'];
      load({
        ...room,
        gameType: 'chog-race',
        status: 'Active',
        state: { ...RACE_BOARD, pawns: [1, 2, 3, 4, 5, 6].map((seat) => ({ seat, tile: 0 })) },
        turnEndsAt: new Date(Date.now() + 20_000).toISOString(),
        turnSeconds: 25,
        members: names.map((name, i) => member(name, { isHost: i === 0, isYou: i === 1, isTurn: i === 0, seat: i + 1 })),
      });

      const board = element().querySelector('.match [data-board="race"]');
      expect(board?.querySelectorAll('[data-tile]').length).toBe(28);
      expect(Array.from(board?.querySelectorAll('[data-tile="0"] [data-pawn]') ?? []).map((p) => p.textContent?.trim())).toEqual(['1', '2', '3', '4', '5', '6']);
      expect(board?.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s Alice’s turn.');
      expect(element().querySelector('h1')?.textContent).toBe('Chog Race room');
      expect(element().querySelector('.match app-turn-timer')).not.toBeNull();
      expect(element().querySelector('[data-list="members"]')).toBeNull();
      expect(element().querySelector('.match')?.classList).toContain('game-wide');
      expect(element().querySelector('[data-board="race"] .side .game-header h1')).not.toBeNull();
      expect(element().querySelector('[data-board="race"] .stage app-turn-timer')).not.toBeNull();
      expect(element().querySelector('[data-started]')?.classList).toContain('visually-hidden');
    });

    it('rolls and ends a Chog Race turn on your turn (STORY-051)', () => {
      const started: Room = {
        ...room,
        version: 4,
        gameType: 'chog-race',
        status: 'Active',
        state: { ...RACE_BOARD, phase: 'Move' },
        members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, isTurn: true })],
      };
      load(started);

      element().querySelector<HTMLButtonElement>('[data-roll]')?.click();
      const roll = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(roll.request.body).toMatchObject({ expectedVersion: 4, type: 'ROLL', payload: {} });
      roll.flush({ actionId: roll.request.body.actionId, type: 'ROLL', version: 5, currentVersion: 5, replayed: false });
      http.expectOne('/api/rooms/K7QF3M').flush({ ...started, version: 5, state: { ...RACE_BOARD, phase: 'Play' } });
      fixture.detectChanges();

      element().querySelector<HTMLButtonElement>('[data-end-turn]')?.click();
      const ended = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(ended.request.body).toMatchObject({ expectedVersion: 5, type: 'END_TURN', payload: {} });
    });

    it('plays a Chog Race card in the Play phase, naming Whale’s target (STORY-053)', () => {
      const started: Room = {
        ...room,
        version: 5,
        gameType: 'chog-race',
        status: 'Active',
        state: { ...RACE_BOARD, phase: 'Play', hand: ['Pump', 'Whale'], playable: ['Pump', 'Whale'] },
        members: [member('Alice', { isHost: true, seat: 1 }), member('Bob', { isYou: true, seat: 2, isTurn: true })],
      };
      load(started);

      element().querySelector<HTMLButtonElement>('[data-play="Whale"]')?.click();
      const whale = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(whale.request.body).toMatchObject({ expectedVersion: 5, type: 'PLAY_CARD', payload: { card: 'Whale', target: 1 } });
      whale.flush({ actionId: whale.request.body.actionId, type: 'PLAY_CARD', version: 6, currentVersion: 6, replayed: false });
      http.expectOne('/api/rooms/K7QF3M').flush(started);
      fixture.detectChanges();

      element().querySelector<HTMLButtonElement>('[data-play="Pump"]')?.click();
      const pump = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(pump.request.body.payload).toEqual({ card: 'Pump' });
    });

    it('answers a Chog Race attack on you, with the clock yours while it waits (STORY-054)', () => {
      const attacked: Room = {
        ...room,
        version: 7,
        gameType: 'chog-race',
        status: 'Active',
        state: { ...RACE_BOARD, phase: 'Reply', reply: { seat: 1, card: 'Rug', target: 2 }, hand: ['Hodl'], playable: [] },
        turnEndsAt: new Date(Date.now() + 20_000).toISOString(),
        turnSeconds: 25,
        members: [member('Alice', { isHost: true, seat: 1, isTurn: true }), member('Bob', { isYou: true, seat: 2 })],
      };
      load(attacked);

      expect(element().querySelector('.match [data-turn-timer] .who')?.textContent).toBe('Your turn');
      element().querySelector<HTMLButtonElement>('[data-block]')?.click();
      const block = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(block.request.body).toMatchObject({ expectedVersion: 7, type: 'PLAY_CARD', payload: { card: 'Hodl' } });
      block.flush({ actionId: block.request.body.actionId, type: 'PLAY_CARD', version: 8, currentVersion: 8, replayed: false });
      http.expectOne('/api/rooms/K7QF3M').flush(attacked);
      fixture.detectChanges();

      element().querySelector<HTMLButtonElement>('[data-pass]')?.click();
      const pass = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(pass.request.body).toMatchObject({ expectedVersion: 7, type: 'PASS', payload: {} });
    });

    it('uses a Chog Race ability on your turn, naming Card Steal’s target (STORY-055)', () => {
      const steal = { id: 'card_steal', name: 'Card Steal', description: '', target: 'Opponent' as const, maxUses: 1, usesLeft: 1 };
      const started: Room = {
        ...room,
        version: 9,
        gameType: 'chog-race',
        status: 'Active',
        state: { ...RACE_BOARD, phase: 'Move', pawns: [{ seat: 1, tile: 0, cards: 2 }, { seat: 2, tile: 0, cards: 0 }], hand: [], maxHand: 3 },
        members: [
          member('Alice', { isHost: true, seat: 1 }),
          member('Bob', { isYou: true, seat: 2, isTurn: true, ability: steal, hasAbility: true }),
        ],
      };
      load(started);

      element().querySelector<HTMLButtonElement>('[data-ability="card_steal"]')?.click();
      const use = http.expectOne('/api/rooms/K7QF3M/commands');
      expect(use.request.body).toMatchObject({ expectedVersion: 9, type: 'USE_ABILITY', payload: { abilityId: 'card_steal', target: 1 } });
    });

    it('counts down a Chog Race to its time cap (STORY-056)', () => {
      load({
        ...room,
        gameType: 'chog-race',
        status: 'Active',
        matchEndsAt: new Date(Date.now() + 754_000).toISOString(),
        state: { ...RACE_BOARD, phase: 'Move' },
        members: [member('Alice', { isHost: true, seat: 1, isTurn: true }), member('Bob', { isYou: true, seat: 2 })],
      });

      expect(element().querySelector('[data-board="race"] [data-clock]')?.textContent?.trim()).toBe('Time left 12:34');
    });

    it('shows the Chog Race results above the final board (STORY-056)', () => {
      const result = (tile: number, extra = {}) => ({ tile, tilesLeft: 27 - tile, finished: tile === 27, cardsPlayed: 0, attacksLanded: 0, attacksBlocked: 0, degenScore: 0, degenScoreVersion: 1, ...extra });
      load({
        ...room,
        gameType: 'chog-race',
        status: 'Completed',
        state: { ...RACE_BOARD, winner: 2, ended: 'Finish' },
        members: [
          member('Alice', { isHost: true, seat: 1, rank: 2, result: result(19, { cardsPlayed: 2 }) }),
          member('Bob', { isYou: true, seat: 2, rank: 1, result: result(27) }),
        ],
      });

      const results = element().querySelector('[data-results]');
      expect(results?.querySelector('[data-winner]')?.textContent).toBe('You win the race!');
      expect(results?.closest('app-race-results')?.nextElementSibling?.matches('[data-final-board]')).toBe(true);
    });

    it('keeps the final Chog Race board once the game is over', () => {
      load({
        ...room,
        gameType: 'chog-race',
        status: 'Completed',
        state: RACE_BOARD,
        members: [member('Alice', { isHost: true, seat: 1, rank: 1 }), member('Bob', { isYou: true, seat: 2, rank: 2 })],
      });

      expect(element().querySelector('[data-final-board] [data-board="race"]')).not.toBeNull();
      // A match from before STORY-056 has no results.
      expect(element().querySelector('[data-results]')).toBeNull();
    });

    it('tells a player who left the game that they are out of it', () => {
      load({ ...room, status: 'Active', members: [member('Alice', { isHost: true }), member('Bob', { isYou: true, hasLeft: true })] });

      expect(element().querySelector('[data-left]')?.textContent).toContain('You left this game.');
      expect(element().querySelector('[data-left] a')?.getAttribute('href')).toBe('/');
      expect(element().querySelector('[data-started]')).toBeNull();
      expect(maybeButton('Leave game')).toBeUndefined();
    });

    it('shows the final standings and congratulates the winner', () => {
      load({
        ...room,
        status: 'Completed',
        members: [
          member('Alice', { isHost: true, isYou: true, rank: 1 }),
          member('Bob', { rank: 3 }),
          member('Carol', { rank: 2, hasLeft: false }),
        ],
      });

      expect(members()).toEqual(['Alice Host You 1st', 'Bob 3rd', 'Carol 2nd']);
      expect(element().querySelector('[data-result]')?.textContent).toContain('The game is over: you won!');
      expect(element().querySelector('[data-result] a')?.getAttribute('href')).toBe('/');
      expect(element().querySelector('[data-started]')).toBeNull();
      expect(maybeButton('Leave game')).toBeUndefined();
    });

    it('tells a player who did not win where they finished, even after leaving', () => {
      load({
        ...room,
        status: 'Completed',
        members: [member('Alice', { isHost: true, rank: 1 }), member('Bob', { rank: 2 }), member('Carol', { isYou: true, hasLeft: true, rank: 3 })],
      });

      expect(members()).toEqual(['Alice Host 1st', 'Bob 2nd', 'Carol You Left 3rd']);
      expect(element().querySelector('[data-result]')?.textContent).toContain('The game is over: you finished 3rd.');
      expect(element().querySelector('[data-left]')).toBeNull();
    });

    it('tells the player which achievements the finished game unlocked for them', () => {
      load({
        ...room,
        status: 'Completed',
        members: [member('Alice', { isHost: true, isYou: true, rank: 1 }), member('Bob', { rank: 2 })],
        achievements: [
          { id: 'first_match', name: 'First Match', description: 'Complete a match.' },
          { id: 'first_win', name: 'First Win', description: 'Win a match.' },
        ],
      });

      const unlocked = element().querySelector('[data-achievements]');
      expect(unlocked?.getAttribute('role')).toBe('status');
      expect(Array.from(unlocked?.querySelectorAll('[data-achievement]') ?? []).map((a) => a.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
        '★ Achievement unlocked: First Match. Complete a match.',
        '★ Achievement unlocked: First Win. Win a match.',
      ]);
      expect(unlocked?.querySelector('a')?.getAttribute('href')).toBe('/profile');
    });

    it('shows no achievements when the game unlocked none for this player', () => {
      load({ ...room, status: 'Completed', members: [member('Alice', { isHost: true, rank: 1 }), member('Bob', { isYou: true, rank: 2 })] });

      expect(element().querySelector('[data-result]')).not.toBeNull();
      expect(element().querySelector('[data-achievements]')).toBeNull();
    });

    it('shows no places before a game is over', () => {
      load({ ...room, status: 'Active', members: [member('Alice', { isHost: true, isYou: true }), member('Bob')] });

      expect(element().querySelector('[data-rank]')).toBeNull();
      expect(element().querySelector('[data-result]')).toBeNull();
    });

    it('tells players when a game ended because too many left', () => {
      load({ ...room, status: 'Abandoned', members: [member('Alice', { isHost: true, hasLeft: true }), member('Bob', { isYou: true })] });

      expect(element().querySelector('[data-closed]')?.textContent).toContain('This game ended early: too many players left.');
      expect(element().querySelector('[data-closed] a')?.getAttribute('href')).toBe('/');
      expect(maybeButton('Leave game')).toBeUndefined();
      expect(maybeButton('Leave room')).toBeUndefined();
    });

    it('tells members when the host has closed the room', () => {
      load({ ...room, status: 'Cancelled' });

      expect(element().querySelector('[data-closed]')?.textContent).toContain('This room is closed.');
      expect(element().querySelector('[data-closed] a')?.getAttribute('href')).toBe('/');
      expect(maybeButton('Leave room')).toBeUndefined();
      expect(maybeButton('I’m ready')).toBeUndefined();
    });
  });
});
