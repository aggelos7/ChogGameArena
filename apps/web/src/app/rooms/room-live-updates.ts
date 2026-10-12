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
 * Live snapshots of one room (STORY-022, STORY-026, DEC-028). Subscribing answers with the player's current snapshot,
 * and the server pushes a new one after every change, each with the room's version; a snapshot may arrive more than
 * once or after a newer one, so the page keeps the highest version (`isNewerRoom`). Whatever was missed while the
 * connection was down is caught up by subscribing again. Subscribing is refused unless the player is a member.
 */
@Injectable({ providedIn: 'root' })
export class RoomLiveUpdates {
  private readonly createConnection = inject(ROOM_HUB_CONNECTION_FACTORY);

  /** Connects while subscribed. `live` is emitted each time the subscription is (re)established, then its snapshot. */
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
        connection.invoke('SubscribeRoom', code).then((room) => {
          emit({ type: 'status', status: 'live' });
          emit({ type: 'snapshot', room: room as Room });
        }, goOffline);

      connection.on('RoomUpdated', (room: Room) => {
        if (room.code === code) {
          emit({ type: 'snapshot', room });
        }
      });
      connection.onreconnecting(() => emit({ type: 'status', status: 'connecting' }));
      // Subscriptions don't survive a reconnect: subscribing again also brings the snapshot missed meanwhile.
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
