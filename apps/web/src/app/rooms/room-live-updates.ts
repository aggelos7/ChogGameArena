import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { HttpTransportType, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Observable } from 'rxjs';
import { Room } from './rooms-api';

/** The room hub (STORY-022). The session cookie authenticates it like any other `/api` request. */
export const ROOM_HUB_PATH = '/api/hubs/rooms';

export type RoomLiveStatus = 'connecting' | 'live' | 'offline';

/** A connection status change, or the player's snapshot of the room: on subscribing and after every change. */
export type RoomLiveEvent = { type: 'status'; status: RoomLiveStatus } | { type: 'snapshot'; room: Room };

/** The part of `@microsoft/signalr`'s `HubConnection` used here, so specs can provide a fake. */
export interface RoomHubConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(methodName: string, ...args: unknown[]): Promise<unknown>;
  on(methodName: string, handler: (...args: never[]) => void): void;
  onclose(callback: () => void): void;
}

export const ROOM_HUB_CONNECTION_FACTORY = new InjectionToken<() => RoomHubConnection>('RoomHubConnectionFactory', {
  providedIn: 'root',
  factory: () => {
    // Absolute, so it resolves the same wherever the client library thinks it runs.
    const url = new URL(ROOM_HUB_PATH, inject(DOCUMENT).baseURI).href;
    return () =>
      new HubConnectionBuilder()
        // WebSockets only, as the server allows; no negotiate round trip is needed then. Reconnecting is done by
        // `RoomLiveUpdates` with a new connection, so it never gives up.
        .withUrl(url, { transport: HttpTransportType.WebSockets, skipNegotiation: true })
        .configureLogging(LogLevel.None)
        .build();
  },
});

/** Waits before each reconnect attempt: at once, then backing off to every 15 seconds for as long as it takes (DEC-029). */
const RETRY_DELAYS_MS = [0, 2_000, 5_000, 10_000, 15_000];

/** Failed attempts shown as `connecting`; after that the page shows live updates as off, while retries go on. */
export const QUIET_RETRIES = 3;

/** The wait before the reconnect attempt that follows `failures` failed ones in a row. */
export function retryDelay(failures: number): number {
  return RETRY_DELAYS_MS[Math.min(Math.max(failures - 1, 0), RETRY_DELAYS_MS.length - 1)];
}

/** The hub's refusal for a room the player isn't in (any more); retrying can't help. */
const ROOM_NOT_FOUND = 'room_not_found';

/**
 * Live snapshots of one room (STORY-022, STORY-026, STORY-027, DEC-028, DEC-029). Subscribing answers with the player's
 * current snapshot, and the server pushes a new one after every change, each with the room's version; a snapshot may
 * arrive more than once or after a newer one, so the page keeps the highest version (`isNewerRoom`).
 *
 * A dropped connection is replaced by a new one, backing off but never giving up, and straight away when the browser
 * comes back online or the tab is shown again; subscribing again catches up whatever was missed. A tab shown again
 * while live subscribes again too, since a hidden tab may have been frozen. Only a refusal (the player is not in the
 * room) ends it for good.
 */
@Injectable({ providedIn: 'root' })
export class RoomLiveUpdates {
  private readonly createConnection = inject(ROOM_HUB_CONNECTION_FACTORY);
  private readonly document = inject(DOCUMENT);

  /** Connects while subscribed. `live` is emitted each time the subscription is (re)established, then its snapshot. */
  watch(code: string): Observable<RoomLiveEvent> {
    return new Observable<RoomLiveEvent>((subscriber) => {
      const window = this.document.defaultView;
      let stopped = false;
      let live = false;
      let failures = 0;
      let connection: RoomHubConnection | null = null;
      let retryTimer: ReturnType<typeof setTimeout> | undefined;

      const emit = (event: RoomLiveEvent) => {
        if (!stopped) {
          subscriber.next(event);
        }
      };

      const subscribe = (current: RoomHubConnection) =>
        current.invoke('SubscribeRoom', code).then(
          (room) => {
            if (connection === current && !stopped) {
              failures = 0;
              live = true;
              emit({ type: 'status', status: 'live' });
              emit({ type: 'snapshot', room: room as Room });
            }
          },
          (error: unknown) => {
            if (connection !== current) {
              return;
            }
            if (String(error).includes(ROOM_NOT_FOUND)) {
              giveUp();
            } else {
              retry();
            }
          },
        );

      const connect = () => {
        clearTimeout(retryTimer);
        retryTimer = undefined;
        if (stopped) {
          return;
        }
        const current = this.createConnection();
        connection = current;
        current.on('RoomUpdated', (room: Room) => {
          if (connection === current && room.code === code) {
            emit({ type: 'snapshot', room });
          }
        });
        current.onclose(() => {
          if (connection === current) {
            retry();
          }
        });
        current.start().then(
          () => (connection === current && !stopped ? subscribe(current) : undefined),
          () => {
            if (connection === current) {
              retry();
            }
          },
        );
      };

      const disconnect = () => {
        const current = connection;
        connection = null;
        live = false;
        void current?.stop();
      };

      const retry = () => {
        disconnect();
        failures++;
        emit({ type: 'status', status: failures <= QUIET_RETRIES ? 'connecting' : 'offline' });
        retryTimer = setTimeout(connect, retryDelay(failures));
      };

      const giveUp = () => {
        emit({ type: 'status', status: 'offline' });
        stop();
      };

      /** Back online or shown again: reconnect now instead of waiting, or catch up on what a frozen tab missed. */
      const wake = () => {
        if (stopped || this.document.visibilityState === 'hidden') {
          return;
        }
        if (live && connection) {
          void subscribe(connection);
        } else if (!connection) {
          connect();
        }
      };

      const stop = () => {
        stopped = true;
        clearTimeout(retryTimer);
        window?.removeEventListener('online', wake);
        this.document.removeEventListener('visibilitychange', wake);
        disconnect();
      };

      window?.addEventListener('online', wake);
      this.document.addEventListener('visibilitychange', wake);
      emit({ type: 'status', status: 'connecting' });
      connect();

      return stop;
    });
  }
}
