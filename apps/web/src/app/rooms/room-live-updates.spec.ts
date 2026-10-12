import { TestBed } from '@angular/core/testing';
import { ROOM_HUB_CONNECTION_FACTORY, RoomHubConnection, RoomLiveEvent, RoomLiveUpdates } from './room-live-updates';
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
  members: [],
  state: null,
});

/** A hand-written stand-in for `HubConnection`: the spec settles its promises and fires its callbacks. */
class FakeConnection implements RoomHubConnection {
  readonly invocations: unknown[][] = [];
  readonly handlers = new Map<string, (...args: never[]) => void>();
  stopped = 0;
  private starting!: { resolve: () => void; reject: (error: Error) => void };
  private invoking: { resolve: (value: unknown) => void; reject: (error: Error) => void }[] = [];
  private reconnecting = () => undefined as void;
  private reconnected = () => undefined as void;
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

  onreconnecting(callback: () => void): void {
    this.reconnecting = callback;
  }

  onreconnected(callback: () => void): void {
    this.reconnected = callback;
  }

  onclose(callback: () => void): void {
    this.closed = callback;
  }

  async started(): Promise<void> {
    this.starting.resolve();
    await flush();
  }

  async startFails(): Promise<void> {
    this.starting.reject(new Error('Unauthorized'));
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

  dropped(): void {
    this.reconnecting();
  }

  async reconnect(): Promise<void> {
    this.reconnected();
    await flush();
  }

  close(): void {
    this.closed();
  }
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve));

describe('RoomLiveUpdates', () => {
  let connections: FakeConnection[];
  let events: RoomLiveEvent[];

  beforeEach(() => {
    connections = [];
    events = [];
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
  });

  const watch = (code = 'K7QF3M') => TestBed.inject(RoomLiveUpdates).watch(code).subscribe((event) => events.push(event));
  const statuses = () => events.flatMap((event) => (event.type === 'status' ? [event.status] : []));
  const versions = () => events.flatMap((event) => (event.type === 'snapshot' ? [event.room.version] : []));

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

  it('subscribes again after reconnecting, which brings the snapshot missed meanwhile', async () => {
    watch();
    const connection = connections[0];
    await connection.started();
    await connection.subscribed(1);

    connection.dropped();
    expect(statuses()).toEqual(['connecting', 'live', 'connecting']);
    await connection.reconnect();
    await connection.subscribed(6);
    expect(versions()).toEqual([1, 6]);

    expect(connection.invocations).toEqual([
      ['SubscribeRoom', 'K7QF3M'],
      ['SubscribeRoom', 'K7QF3M'],
    ]);
    expect(statuses()).toEqual(['connecting', 'live', 'connecting', 'live']);
  });

  it('goes offline when the connection is refused, closes or the room is not the player’s', async () => {
    watch();
    await connections[0].startFails();
    expect(statuses()).toEqual(['connecting', 'offline']);

    watch('ZZZZZZ');
    await connections[1].started();
    await connections[1].subscribeRefused();
    expect(statuses().slice(2)).toEqual(['connecting', 'offline']);
    expect(connections[1].stopped).toBe(1);

    watch();
    await connections[2].started();
    await connections[2].subscribed();
    connections[2].close();
    expect(statuses().slice(4)).toEqual(['connecting', 'live', 'offline']);
  });

  it('disconnects when no longer watched and stays quiet afterwards', async () => {
    const subscription = watch();
    const connection = connections[0];
    await connection.started();
    await connection.subscribed();

    subscription.unsubscribe();
    connection.roomUpdated(snapshot('K7QF3M', 2));
    connection.close();

    expect(connection.stopped).toBe(1);
    expect(events).toEqual([
      { type: 'status', status: 'connecting' },
      { type: 'status', status: 'live' },
      { type: 'snapshot', room: snapshot('K7QF3M', 1) },
    ]);
  });
});
