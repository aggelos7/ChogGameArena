import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** Mirrors `RoomCharacterResponse`: the member's selected Chog. `color` is set for standard Chogs only. */
export interface RoomCharacter {
  kind: 'Standard' | 'Nft';
  id: string;
  name: string;
  color: string | null;
}

/** Mirrors `RoomMemberResponse`. Player ids never leave the server. */
export interface RoomMember {
  displayName: string;
  avatarId: string;
  isHost: boolean;
  isYou: boolean;
  /** Ready with the Chog shown; choosing another Chog makes the player not ready. */
  isReady: boolean;
  character: RoomCharacter;
  /** Always null until ability selection (STORY-033). */
  ability: string | null;
}

export type RoomStatus = 'Waiting' | 'Active' | 'Completed' | 'Cancelled';

/** Mirrors `RoomResponse` from the `/api/rooms` endpoints. */
export interface Room {
  code: string;
  gameType: string;
  status: RoomStatus;
  minPlayers: number;
  capacity: number;
  members: RoomMember[];
}

/** Stable `code` values of room errors (DEC-021). */
export type RoomErrorCode =
  | 'unknown_game'
  | 'game_unavailable'
  | 'already_in_room'
  | 'room_not_found'
  | 'room_full'
  | 'room_started'
  | 'room_closed'
  | 'not_host'
  | 'not_enough_players'
  | 'players_not_ready'
  | 'character_unavailable'
  | 'ownership_unavailable';

/** A room error from the server, or `unknown` for anything else (network, 5xx). */
export interface RoomError {
  code: RoomErrorCode | 'rate_limited' | 'unknown';
  /** For `already_in_room`: the room the player is already in. */
  roomCode?: string;
}

/** Room codes are six characters without look-alikes (no 0, O, 1, I or L); case does not matter. */
export const ROOM_CODE_PATTERN = /^[2-9A-HJKMNP-Z]{6}$/;

export function normalizeRoomCode(input: string): string {
  return input.trim().toUpperCase();
}

export function toRoomError(error: unknown): RoomError {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 429) {
      return { code: 'rate_limited' };
    }
    const body = error.error as { code?: unknown; roomCode?: unknown } | null;
    if (typeof body?.code === 'string') {
      return { code: body.code as RoomErrorCode, roomCode: typeof body.roomCode === 'string' ? body.roomCode : undefined };
    }
  }
  return { code: 'unknown' };
}

const ROOM_ERROR_MESSAGES: Record<RoomError['code'], string> = {
  unknown_game: 'This game does not exist.',
  game_unavailable: 'This game is not available right now.',
  already_in_room: 'You are already in a room.',
  room_not_found: 'No room with this code was found.',
  room_full: 'This room is full.',
  room_started: 'This room’s game has already started.',
  room_closed: 'This room is closed.',
  not_host: 'Only the host can start the game.',
  not_enough_players: 'More players are needed to start.',
  players_not_ready: 'Every player must be ready to start.',
  character_unavailable: 'A player no longer owns their chosen Chog and must choose again.',
  ownership_unavailable: 'Chog ownership can’t be checked right now. Try again shortly.',
  rate_limited: 'Too many tries in a short time. Wait a minute and try again.',
  unknown: 'Something went wrong. Try again.',
};

export function roomErrorMessage(error: RoomError): string {
  return ROOM_ERROR_MESSAGES[error.code] ?? ROOM_ERROR_MESSAGES.unknown;
}

/** The host and members always come from the session cookie; nothing here identifies the player. */
@Injectable({ providedIn: 'root' })
export class RoomsApi {
  private readonly http = inject(HttpClient);

  create(gameType: string): Observable<Room> {
    return this.http.post<Room>('/api/rooms', { gameType });
  }

  join(code: string): Observable<Room> {
    return this.http.post<Room>(`/api/rooms/${encodeURIComponent(code)}/join`, null);
  }

  get(code: string): Observable<Room> {
    return this.http.get<Room>(`/api/rooms/${encodeURIComponent(code)}`);
  }

  setReady(code: string, ready: boolean): Observable<Room> {
    return this.http.put<Room>(`/api/rooms/${encodeURIComponent(code)}/ready`, { ready });
  }

  start(code: string): Observable<Room> {
    return this.http.post<Room>(`/api/rooms/${encodeURIComponent(code)}/start`, null);
  }

  /** Leaves a waiting room; when the host leaves, the room is closed for everyone. */
  leave(code: string): Observable<void> {
    return this.http.post<void>(`/api/rooms/${encodeURIComponent(code)}/leave`, null);
  }
}
