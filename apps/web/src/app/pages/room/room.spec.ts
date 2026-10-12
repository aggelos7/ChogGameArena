import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { RoomLiveEvent, RoomLiveUpdates } from '../../rooms/room-live-updates';
import { Room, RoomMember } from '../../rooms/rooms-api';
import { CATALOGUE } from '../../testing';
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
      isHost: false,
      isYou: false,
      isReady: false,
      character: { kind: 'Standard', id: 'classic', name: 'Classic', color: '#5b2bd6' },
      ability: null,
      hasLeft: false,
      isTurn: false,
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
    members: [
      member('Alice', { isHost: true }),
      member('Bob', { isYou: true, character: { kind: 'Standard', id: 'mint', name: 'Mint', color: '#1a7f5a' } }),
    ],
    state: null,
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

  it('shows each player’s Chog, the ability placeholder and who is ready', () => {
    load(hostView({}, [false, true]));

    expect(column('[data-chog]')).toEqual(['Classic', 'Chog #7']);
    expect(column('[data-ability]')).toEqual(['Ability not chosen yet', 'Ability not chosen yet']);
    expect(column('[data-readiness]')).toEqual(['Not ready', 'Ready']);
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

    it('tells a player who left the game that they are out of it', () => {
      load({ ...room, status: 'Active', members: [member('Alice', { isHost: true }), member('Bob', { isYou: true, hasLeft: true })] });

      expect(element().querySelector('[data-left]')?.textContent).toContain('You left this game.');
      expect(element().querySelector('[data-left] a')?.getAttribute('href')).toBe('/games');
      expect(element().querySelector('[data-started]')).toBeNull();
      expect(maybeButton('Leave game')).toBeUndefined();
    });

    it('tells players when a game ended because too many left', () => {
      load({ ...room, status: 'Abandoned', members: [member('Alice', { isHost: true, hasLeft: true }), member('Bob', { isYou: true })] });

      expect(element().querySelector('[data-closed]')?.textContent).toContain('This game ended early: too many players left.');
      expect(element().querySelector('[data-closed] a')?.getAttribute('href')).toBe('/games');
      expect(maybeButton('Leave game')).toBeUndefined();
      expect(maybeButton('Leave room')).toBeUndefined();
    });

    it('tells members when the host has closed the room', () => {
      load({ ...room, status: 'Cancelled' });

      expect(element().querySelector('[data-closed]')?.textContent).toContain('This room is closed.');
      expect(element().querySelector('[data-closed] a')?.getAttribute('href')).toBe('/games');
      expect(maybeButton('Leave room')).toBeUndefined();
      expect(maybeButton('I’m ready')).toBeUndefined();
    });
  });
});
