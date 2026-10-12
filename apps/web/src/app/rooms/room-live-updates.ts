import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { HttpTransportType, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Observable } from 'rxjs';

/** The room hub (STORY-022). The session cookie authenticates it like any other `/api` request. */
export const ROOM_HUB_PATH = '/api/hubs/rooms';

export type RoomLiveStatus = 'connecting' | 'live' | 'offline';

/** A connection status change, or a notice that the room changed and should be fetched again. */
export type RoomLiveEvent = { type: 'status'; status: RoomLiveStatus } | { type: 'changed' };

/** The part of `@microsoft/signalr`'s `HubConnection` used here, so specs can provide a fake. */
export interface RoomHubConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(methodName: string, ...args: unknown[]): Promise<unknown>;
  on(methodName: string, handler: (...args: never[]) => void): void;
  onreconnecting(callback: () => void): void;
  onreconnected(callback: () => void): void;
  onclose(callback: () => void): void;
}

export const ROOM_HUB_CONNECTION_FACTORY = new InjectionToken<() => RoomHubConnection>('RoomHubConnectionFactory', {
  providedIn: 'root',
  factory: () => {
    // Absolute, so it resolves the same wherever the client library thinks it runs.
    const url = new URL(ROOM_HUB_PATH, inject(DOCUMENT).baseURI).href;
    return () =>
      new HubConnectionBuilder()
        // WebSockets only, as the server allows; no negotiate round trip is needed then.
        .withUrl(url, { transport: HttpTransportType.WebSockets, skipNegotiation: true })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.None)
        .build();
  },
});

/**
 * Live notices for one room (STORY-022, DEC-024). The server only says that the room changed; the room itself is
 * always fetched over REST, so this carries no state of its own. Subscribing is refused unless the player is a member.
 */
@Injectable({ providedIn: 'root' })
export class RoomLiveUpdates {
  private readonly createConnection = inject(ROOM_HUB_CONNECTION_FACTORY);

  /** Connects while subscribed. `live` is emitted each time the subscription is (re)established. */
  watch(code: string): Observable<RoomLiveEvent> {
    return new Observable<RoomLiveEvent>((subscriber) => {
      const connection = this.createConnection();
      let stopped = false;
      const emit = (event: RoomLiveEvent) => {
        if (!stopped) {
          subscriber.next(event);
        }
      };
      const goOffline = () => {
        emit({ type: 'status', status: 'offline' });
        stopped = true;
        void connection.stop();
      };
      const subscribe = () =>
        connection.invoke('SubscribeRoom', code).then(() => emit({ type: 'status', status: 'live' }), goOffline);

      connection.on('RoomChanged', (changed: string) => {
        if (changed === code) {
          emit({ type: 'changed' });
        }
      });
      connection.onreconnecting(() => emit({ type: 'status', status: 'connecting' }));
      // Groups don't survive a reconnect: subscribe again, and the page fetches what it missed on `live`.
      connection.onreconnected(() => void subscribe());
      connection.onclose(() => emit({ type: 'status', status: 'offline' }));

      emit({ type: 'status', status: 'connecting' });
      connection.start().then(() => (stopped ? undefined : subscribe()), goOffline);

      return () => {
        stopped = true;
        void connection.stop();
      };
    });
  }
}
