import { DOCUMENT } from '@angular/common';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GameCatalog } from '../../games/game-catalog';
import { Observable } from 'rxjs';
import { Avatar } from '../../profile/profile-api';
import { AvatarBadge } from '../../profile/avatar-badge';
import {
  Room,
  RoomCharacter,
  RoomError,
  RoomsApi,
  normalizeRoomCode,
  roomErrorMessage,
  toRoomError,
} from '../../rooms/rooms-api';
import { ErrorState, LoadingState } from '../../shared/ui/state-views';

/** NFT Chogs have no colour of their own here; they use the brand colour. */
const NFT_COLOR = 'var(--brand)';

/**
 * A room the player is in and its lobby (STORY-021): code, invite link, members with their Chog, ability and ready
 * status, the player's ready toggle and the host's start. Refreshed on demand until live updates (STORY-022).
 * The server decides who may start; the button state here is only a hint.
 */
@Component({
  selector: 'app-room',
  imports: [RouterLink, LoadingState, ErrorState, AvatarBadge],
  templateUrl: './room.html',
  styleUrl: './room.scss',
})
export class RoomPage implements OnInit {
  private readonly api = inject(RoomsApi);
  private readonly catalog = inject(GameCatalog);
  private readonly document = inject(DOCUMENT);

  readonly code = input.required<string>();

  protected readonly room = signal<Room | null>(null);
  protected readonly loadState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly error = signal<RoomError | null>(null);
  protected readonly gameName = signal<string | null>(null);
  protected readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  protected readonly busy = signal(false);
  protected readonly actionError = signal<RoomError | null>(null);
  protected readonly message = roomErrorMessage;

  protected readonly you = computed(() => this.room()?.members.find((member) => member.isYou) ?? null);

  protected readonly canStart = computed(() => {
    const room = this.room();
    return (
      !!room &&
      room.status === 'Waiting' &&
      !!this.you()?.isHost &&
      room.members.length >= room.minPlayers &&
      room.members.every((member) => member.isReady)
    );
  });

  protected readonly inviteLink = computed(() => {
    const room = this.room();
    return room ? `${this.document.location.origin}/join/${room.code}` : '';
  });

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loadState.set('loading');
    this.copyState.set('idle');
    this.actionError.set(null);
    this.api.get(normalizeRoomCode(this.code())).subscribe({
      next: (room) => {
        this.room.set(room);
        this.loadState.set('ready');
        this.loadGameName(room.gameType);
      },
      error: (error: unknown) => {
        this.error.set(toRoomError(error));
        this.loadState.set('error');
      },
    });
  }

  protected toggleReady(): void {
    const room = this.room();
    if (room) {
      this.act(this.api.setReady(room.code, !this.you()?.isReady));
    }
  }

  protected start(): void {
    const room = this.room();
    if (room) {
      this.act(this.api.start(room.code));
    }
  }

  protected badge(character: RoomCharacter): Avatar {
    return { id: character.id, name: character.name, color: character.color ?? NFT_COLOR };
  }

  private act(request: Observable<Room>): void {
    this.busy.set(true);
    this.actionError.set(null);
    request.subscribe({
      next: (room) => {
        this.room.set(room);
        this.busy.set(false);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        const roomError = toRoomError(error);
        // The lobby changed under us (someone un-readied, joined or started): show it as it is now.
        if (['players_not_ready', 'character_unavailable', 'not_enough_players', 'room_started', 'room_closed'].includes(roomError.code)) {
          this.refreshQuietly();
        }
        this.actionError.set(roomError);
      },
    });
  }

  private refreshQuietly(): void {
    const room = this.room();
    if (room) {
      this.api.get(room.code).subscribe({ next: (current) => this.room.set(current), error: () => undefined });
    }
  }

  protected async copyLink(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.inviteLink());
      this.copyState.set('copied');
    } catch {
      this.copyState.set('failed');
    }
  }

  private loadGameName(gameType: string): void {
    if (this.gameName()) {
      return;
    }
    // Only a label: the room itself is already authorized by the server.
    this.catalog.getGames().subscribe({
      next: (games) => this.gameName.set(games.find((game) => game.id === gameType)?.name ?? null),
      error: () => this.gameName.set(null),
    });
  }
}
