import { DOCUMENT } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { GameCatalog } from '../../games/game-catalog';
import { Observable, Subject, Subscription, catchError, of, switchMap } from 'rxjs';
import { Avatar } from '../../profile/profile-api';
import { AvatarBadge } from '../../profile/avatar-badge';
import {
  Room,
  RoomCharacter,
  RoomError,
  RoomsApi,
  isNewerRoom,
  normalizeRoomCode,
  roomErrorMessage,
  toRoomError,
} from '../../rooms/rooms-api';
import { RoomLiveStatus, RoomLiveUpdates } from '../../rooms/room-live-updates';
import { ErrorState, LoadingState } from '../../shared/ui/state-views';

/** NFT Chogs have no colour of their own here; they use the brand colour. */
const NFT_COLOR = 'var(--brand)';

/**
 * A room the player is in and its lobby (STORY-021): code, invite link, members with their Chog, ability and ready
 * status, the player's ready toggle, the host's start and leaving (STORY-063). Once started it shows each player's locked
 * Chog, and leaving forfeits the game (STORY-023, DEC-025). It follows the room live (STORY-022, STORY-026): the
 * server pushes a versioned snapshot after every change and the page keeps the newest one it has seen, from the push or
 * a request alike, so an older answer never wins. Refresh is offered only while live updates are down.
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
  private readonly router = inject(Router);
  private readonly live = inject(RoomLiveUpdates);
  private readonly destroyRef = inject(DestroyRef);
  /** Quiet re-fetches after a refused action; a newer one replaces one still in flight. */
  private readonly refreshes = new Subject<string>();
  private liveSubscription?: Subscription;

  readonly code = input.required<string>();

  protected readonly room = signal<Room | null>(null);
  protected readonly loadState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly error = signal<RoomError | null>(null);
  protected readonly gameName = signal<string | null>(null);
  protected readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  protected readonly busy = signal(false);
  protected readonly actionError = signal<RoomError | null>(null);
  /** Confirmed first when leaving costs something: the host closes the room for everyone; a player forfeits a started game. */
  protected readonly confirmingLeave = signal(false);
  protected readonly liveState = signal<RoomLiveStatus>('connecting');
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

  constructor() {
    this.refreshes
      .pipe(
        switchMap((code) => this.api.get(code).pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe((current) => {
        if (current) {
          this.show(current);
        }
      });
    this.destroyRef.onDestroy(() => this.liveSubscription?.unsubscribe());
  }

  ngOnInit(): void {
    this.load();
    this.followLive();
  }

  /** Refresh while live updates are down: loads the room and tries to go live again. */
  protected reconnect(): void {
    this.load();
    this.followLive();
  }

  private followLive(): void {
    this.liveSubscription?.unsubscribe();
    this.liveSubscription = this.live.watch(normalizeRoomCode(this.code())).subscribe((event) => {
      // Subscribing again after a drop brings a snapshot too, so nothing missed meanwhile needs fetching.
      if (event.type === 'status') {
        this.liveState.set(event.status);
      } else {
        this.show(event.room);
      }
    });
  }

  protected load(): void {
    this.loadState.set('loading');
    this.copyState.set('idle');
    this.actionError.set(null);
    this.api.get(normalizeRoomCode(this.code())).subscribe({
      next: (room) => {
        this.show(room);
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

  protected leave(): void {
    const room = this.room();
    if (!room) {
      return;
    }
    const costly = room.status === 'Active' || !!this.you()?.isHost;
    if (costly && !this.confirmingLeave()) {
      this.confirmingLeave.set(true);
      return;
    }
    this.busy.set(true);
    this.actionError.set(null);
    this.api.leave(room.code).subscribe({
      next: () => this.router.navigate(['/games', room.gameType]),
      error: (error: unknown) => {
        this.busy.set(false);
        this.confirmingLeave.set(false);
        this.actionError.set(toRoomError(error));
        this.refreshQuietly();
      },
    });
  }

  protected badge(character: RoomCharacter): Avatar {
    return { id: character.id, name: character.name, color: character.color ?? NFT_COLOR };
  }

  private act(request: Observable<Room>): void {
    this.busy.set(true);
    this.actionError.set(null);
    request.subscribe({
      next: (room) => {
        this.show(room);
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

  /** Shows `room` unless a newer snapshot is already shown (DEC-028). */
  private show(room: Room): void {
    if (isNewerRoom(room, this.room())) {
      this.room.set(room);
    }
  }

  private refreshQuietly(): void {
    const room = this.room();
    if (room) {
      this.refreshes.next(room.code);
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
