import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/**
 * Mirrors `RoomCharacterResponse`: the member's selected Chog, or the one locked at start once the game has started.
 * `color` is set for standard Chogs only.
 */
export interface RoomCharacter {
  kind: 'Standard' | 'Nft';
  id: string;
  name: string;
  color: string | null;
}

/**
 * Mirrors `RoomAbilityResponse`: an ability of the game, as offered in the lobby or as locked for a player at the
 * start (DEC-035).
 */
export interface RoomAbility {
  id: string;
  name: string;
  description: string;
  target: 'None' | 'Opponent';
  maxUses: number;
  /** For a locked ability: uses left in this game (DEC-036); null for a lobby choice. */
  usesLeft: number | null;
}

/** Mirrors `RoomAchievementResponse`: an achievement this player unlocked with the completed match (DEC-063). */
export interface RoomAchievement {
  id: string;
  name: string;
  description: string;
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
  /**
   * The player's ability: your own choice while waiting (others' stay hidden), then everyone's locked one once the game
   * has started (DEC-035). Null when there is none to show.
   */
  ability: RoomAbility | null;
  /** Has chosen an ability (or, once started, locked one): all others see before the start. */
  hasAbility: boolean;
  /** Left the started match: a forfeit (DEC-025). */
  hasLeft: boolean;
  /** It is this member's turn in the active match (DEC-027). */
  isTurn: boolean;
  /** Final place in a completed match, from 1 for the winners (ties share a place); null otherwise (DEC-030). */
  rank: number | null;
  /** Place in the turn order from 1, given at the start; how abilities name their target (DEC-036). Null before it. */
  seat: number | null;
  /**
   * The game's record of how the member did in a completed match, stored with their rank and the same for everyone
   * (STORY-043, DEC-046); its shape is the game's own. Null for players who left and in every other status.
   */
  result: unknown;
}

/** `Cancelled`: closed before it started. `Abandoned`: started, then too many players left (DEC-025). */
export type RoomStatus = 'Waiting' | 'Active' | 'Completed' | 'Cancelled' | 'Abandoned';

/**
 * Mirrors `RoomResponse`: one player's snapshot of the room, from the `/api/rooms` endpoints or pushed live (DEC-028).
 * `version` is bumped by every change, so the snapshot with the highest version is the current one.
 */
export interface Room {
  code: string;
  matchId: string;
  version: number;
  gameType: string;
  status: RoomStatus;
  minPlayers: number;
  capacity: number;
  /** The turn being played, from 1; 0 before the start. */
  turnNumber: number;
  /** When the current turn runs out (ISO 8601); null unless the match is active. */
  turnEndsAt: string | null;
  /** How long each turn of this match lasts, in seconds (DEC-045); null before the start. */
  turnSeconds: number | null;
  /**
   * When the match's time is up (ISO 8601, STORY-041, ENG-011), kept once it ends; null before the start and for games
   * without a time limit.
   */
  matchEndsAt: string | null;
  members: RoomMember[];
  /** What this player may see of the game's own state; null until the game has any. */
  state: unknown;
  /** What this player may choose from while the room waits; empty for a game without abilities and once started. */
  abilities: RoomAbility[];
  /** Achievements this player unlocked with the completed match (STORY-058); empty otherwise and for everyone else's. */
  achievements: RoomAchievement[];
}

/** Mirrors `MatchCommandResponse`: the room's `version` after the command and whether it was a replayed retry. */
export interface CommandResult {
  actionId: string;
  type: string;
  version: number;
  currentVersion: number;
  replayed: boolean;
}

/** Whether `next` is at least as recent as `current`, so it may replace it; snapshots of other rooms never are. */
export function isNewerRoom(next: Room, current: Room | null): boolean {
  return !current || (next.matchId === current.matchId && next.version >= current.version);
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
  | 'ownership_unavailable'
  | 'unknown_ability'
  | 'ability_required'
  | 'ability_unavailable'
  | 'not_your_turn'
  | 'turn_expired'
  | 'time_up'
  | 'stale_version'
  | 'match_not_started'
  | 'not_in_match'
  | 'unknown_command'
  | 'command_rejected'
  | 'action_conflict';

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
  unknown_ability: 'That ability can’t be chosen in this game.',
  ability_required: 'Choose an ability before marking ready.',
  ability_unavailable: 'A player’s chosen ability is no longer available and they must choose again.',
  not_your_turn: 'It isn’t your turn.',
  turn_expired: 'Your turn has run out.',
  time_up: 'Time’s up: the game is ending.',
  stale_version: 'The game moved on before your move arrived. Here’s where it is now.',
  match_not_started: 'The game hasn’t started yet.',
  not_in_match: 'You have left this game.',
  unknown_command: 'That move doesn’t exist in this game.',
  command_rejected: 'That move isn’t allowed right now.',
  action_conflict: 'That move was already sent differently. Try again.',
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

  /**
   * The player's open room (a waiting room they're in or a game they still play), or null when there is none
   * (`204`): where to return after a refresh, a lost connection or on another device (STORY-027, DEC-029).
   */
  current(): Observable<Room | null> {
    return this.http.get<Room | null>('/api/rooms/current');
  }

  get(code: string): Observable<Room> {
    return this.http.get<Room>(`/api/rooms/${encodeURIComponent(code)}`);
  }

  setReady(code: string, ready: boolean): Observable<Room> {
    return this.http.put<Room>(`/api/rooms/${encodeURIComponent(code)}/ready`, { ready });
  }

  /** Chooses the ability for the game; choosing another one than before makes the player not ready (DEC-035). */
  selectAbility(code: string, abilityId: string): Observable<Room> {
    return this.http.put<Room>(`/api/rooms/${encodeURIComponent(code)}/ability`, { abilityId });
  }

  start(code: string): Observable<Room> {
    return this.http.post<Room>(`/api/rooms/${encodeURIComponent(code)}/start`, null);
  }

  /**
   * Sends a game command (DEC-026), such as Chog Life's `ROLL`, for the version the player saw; a new `actionId` each
   * time, so a retry of the same request is replayed rather than played twice. The result arrives as the next room
   * snapshot (DEC-028).
   */
  sendCommand(room: Room, type: string, payload: object = {}, actionId: string = crypto.randomUUID()): Observable<CommandResult> {
    return this.http.post<CommandResult>(`/api/rooms/${encodeURIComponent(room.code)}/commands`, {
      actionId,
      matchId: room.matchId,
      expectedVersion: room.version,
      type,
      payload,
    });
  }

  /**
   * Leaves the room. In a waiting room the host leaving closes it for everyone; leaving a started game forfeits it
   * (DEC-025).
   */
  leave(code: string): Observable<void> {
    return this.http.post<void>(`/api/rooms/${encodeURIComponent(code)}/leave`, null);
  }
}
