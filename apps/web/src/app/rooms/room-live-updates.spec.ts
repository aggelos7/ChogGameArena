import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import {
  QUIET_RETRIES,
  ROOM_HUB_CONNECTION_FACTORY,
  RoomHubConnection,
  RoomLiveEvent,
  RoomLiveUpdates,
  retryDelay,
} from './room-live-updates';
import { Room } from './rooms-api';

const snapshot = (code: string, version: number): Room => ({
  code,
  matchId: '5f0c9a8e-3d4b-4c1a-9e2f-7a6b5c4d3e2f',
  version,
  gameType: 'chog-life',
  status: 'Waiting',
  minPlayers: 2,
  capacity: 6,
  turnNumber: 0,
  turnEndsAt: null,
  matchEndsAt: null,
  members: [],
  state: null,
  abilities: [],
});

/** Runs pending promise callbacks and timers that are due now; the spec moves the fake clock itself. */
const flush = () => vi.advanceTimersByTimeAsync(0);

/** A hand-written stand-in for `HubConnection`: the spec settles its promises and fires its callbacks. */
class FakeConnection implements RoomHubConnection {
  readonly invocations: unknown[][] = [];
  readonly handlers = new Map<string, (...args: never[]) => void>();
  stopped = 0;
  private starting!: { resolve: () => void; reject: (error: Error) => void };
  private invoking: { resolve: (value: unknown) => void; reject: (error: Error) => void }[] = [];
  private closed = () => undefined as void;

  start(): Promise<void> {
    return new Promise((resolve, reject) => (this.starting = { resolve, reject }));
  }

  stop(): Promise<void> {
    this.stopped++;
    return Promise.resolve();
  }

  invoke(methodName: string, ...args: unknown[]): Promise<unknown> {
    this.invocations.push([methodName, ...args]);
    return new Promise((resolve, reject) => this.invoking.push({ resolve, reject }));
  }

  on(methodName: string, handler: (...args: never[]) => void): void {
    this.handlers.set(methodName, handler);
  }

  onclose(callback: () => void): void {
    this.closed = callback;
  }

  async started(): Promise<void> {
    this.starting.resolve();
    await flush();
  }

  async startFails(): Promise<void> {
    this.starting.reject(new Error('Failed to connect'));
    await flush();
  }

  /** The server accepts the subscription and answers with the room's current snapshot. */
  async subscribed(version = 1): Promise<void> {
    this.pendingInvocation().resolve(snapshot('K7QF3M', version));
    await flush();
  }

  async subscribeRefused(): Promise<void> {
    this.pendingInvocation().reject(new Error('HubException: room_not_found'));
    await flush();
  }

  private pendingInvocation() {
    const pending = this.invoking.shift();
    if (!pending) {
      throw new Error('No hub call is waiting.');
    }
    return pending;
  }

  roomUpdated(room: Room): void {
    (this.handlers.get('RoomUpdated') as (room: Room) => void)(room);
  }

  /** The connection is lost (network, server restart). */
  async close(): Promise<void> {
    this.closed();
    await flush();
  }
}

describe('RoomLiveUpdates', () => {
  let connections: FakeConnection[];
  let events: RoomLiveEvent[];
  let visibility: DocumentVisibilityState;

  beforeEach(() => {
    vi.useFakeTimers();
    connections = [];
    events = [];
    visibility = 'visible';
    TestBed.configureTestingModule({
      providers: [
        {
          provide: ROOM_HUB_CONNECTION_FACTORY,
          useValue: () => {
            const connection = new FakeConnection();
            connections.push(connection);
            return connection;
          },
        },
      ],
    });
    vi.spyOn(TestBed.inject(DOCUMENT), 'visibilityState', 'get').mockImplementation(() => visibility);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  const watch = (code = 'K7QF3M') => TestBed.inject(RoomLiveUpdates).watch(code).subscribe((event) => events.push(event));
  const statuses = () => events.flatMap((event) => (event.type === 'status' ? [event.status] : []));
  const lastStatus = () => statuses()[statuses().length - 1];
  const versions = () => events.flatMap((event) => (event.type === 'snapshot' ? [event.room.version] : []));
  const latest = () => connections[connections.length - 1];

  /** Watches the room and goes live with `version`. */
  const live = async (version = 1) => {
    const subscription = watch();
    await latest().started();
    await latest().subscribed(version);
    return subscription;
  };

  const shown = async (state: DocumentVisibilityState) => {
    visibility = state;
    TestBed.inject(DOCUMENT).dispatchEvent(new Event('visibilitychange'));
    await flush();
  };

  it('subscribes once connected, then passes on the subscription’s snapshot and this room’s pushed ones only', async () => {
    watch();
    const connection = connections[0];
    expect(statuses()).toEqual(['connecting']);

    await connection.started();
    expect(connection.invocations).toEqual([['SubscribeRoom', 'K7QF3M']]);
    await connection.subscribed(3);
    expect(events).toEqual([
      { type: 'status', status: 'connecting' },
      { type: 'status', status: 'live' },
      { type: 'snapshot', room: snapshot('K7QF3M', 3) },
    ]);

    connection.roomUpdated(snapshot('K7QF3M', 4));
    connection.roomUpdated(snapshot('ZZZZZZ', 5));
    expect(versions()).toEqual([3, 4]);
  });

  it('reconnects at once after a drop and subscribes again, which brings the snapshot missed meanwhile', async () => {
    await live(1);
    const dropped = connections[0];

    await dropped.close();
    expect(statuses()).toEqual(['connecting', 'live', 'connecting']);
    expect(dropped.stopped).toBe(1);
    expect(connections).toHaveLength(2);

    await latest().started();
    expect(latest().invocations).toEqual([['SubscribeRoom', 'K7QF3M']]);
    await latest().subscribed(6);
    expect(versions()).toEqual([1, 6]);
    expect(statuses()).toEqual(['connecting', 'live', 'connecting', 'live']);

    // The old connection is done with, whatever it still delivers.
    dropped.roomUpdated(snapshot('K7QF3M', 2));
    expect(versions()).toEqual([1, 6]);
  });

  it('never gives up: backs off to every 15 seconds and shows live updates as off after a few tries', async () => {
    expect([1, 2, 3, 4, 5, 6, 7].map(retryDelay)).toEqual([0, 2_000, 5_000, 10_000, 15_000, 15_000, 15_000]);

    watch();
    for (let failure = 1; failure <= 7; failure++) {
      const before = connections.length;
      await latest().startFails();
      if (retryDelay(failure) > 0) {
        await vi.advanceTimersByTimeAsync(retryDelay(failure) - 1);
        expect(connections).toHaveLength(before);
        await vi.advanceTimersByTimeAsync(1);
      }
      expect(connections).toHaveLength(before + 1);
    }

    expect(statuses()).toEqual(['connecting', ...Array(QUIET_RETRIES).fill('connecting'), 'offline', 'offline', 'offline', 'offline']);

    // The server is back: live again, and the next drop starts from a quick retry.
    await latest().started();
    await latest().subscribed(9);
    expect(lastStatus()).toBe('live');
    await latest().close();
    expect(lastStatus()).toBe('connecting');
    expect(connections).toHaveLength(9);
  });

  it('retries right away when the browser is back online or the tab is shown again', async () => {
    watch();
    await latest().startFails();
    await latest().startFails();
    expect(connections).toHaveLength(2);

    window.dispatchEvent(new Event('online'));
    await flush();
    expect(connections).toHaveLength(3);

    // Already trying: no second connection.
    window.dispatchEvent(new Event('online'));
    await flush();
    expect(connections).toHaveLength(3);

    await latest().startFails();
    await shown('hidden');
    expect(connections).toHaveLength(3);
    await shown('visible');
    expect(connections).toHaveLength(4);
    await latest().started();
    await latest().subscribed(4);
    expect(lastStatus()).toBe('live');
  });

  it('catches up when a live tab is shown again, since a hidden tab may have been frozen', async () => {
    await live(2);

    await shown('visible');
    await latest().subscribed(5);

    expect(connections).toHaveLength(1);
    expect(connections[0].invocations).toEqual([
      ['SubscribeRoom', 'K7QF3M'],
      ['SubscribeRoom', 'K7QF3M'],
    ]);
    expect(versions()).toEqual([2, 5]);
  });

  it('stops for good when the room is not the player’s', async () => {
    watch('ZZZZZZ');
    await latest().started();
    await latest().subscribeRefused();

    expect(statuses()).toEqual(['connecting', 'offline']);
    expect(connections[0].stopped).toBe(1);

    window.dispatchEvent(new Event('online'));
    await vi.advanceTimersByTimeAsync(60_000);
    expect(connections).toHaveLength(1);
  });

  it('disconnects when no longer watched and stays quiet afterwards', async () => {
    const subscription = await live(1);
    const connection = connections[0];

    subscription.unsubscribe();
    connection.roomUpdated(snapshot('K7QF3M', 2));
    await connection.close();
    window.dispatchEvent(new Event('online'));
    await vi.advanceTimersByTimeAsync(60_000);

    expect(connection.stopped).toBe(1);
    expect(connections).toHaveLength(1);
    expect(events).toEqual([
      { type: 'status', status: 'connecting' },
      { type: 'status', status: 'live' },
      { type: 'snapshot', room: snapshot('K7QF3M', 1) },
    ]);
  });

  it('stops retrying when no longer watched while waiting to retry', async () => {
    const subscription = watch();
    await latest().startFails();
    await latest().startFails();

    subscription.unsubscribe();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(connections).toHaveLength(2);
  });
});
