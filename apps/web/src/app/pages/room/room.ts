import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { GameCatalog } from '../../games/game-catalog';
import { Observable, Subject, Subscription, catchError, of, switchMap } from 'rxjs';
import { Avatar } from '../../profile/profile-api';
import { AvatarBadge } from '../../profile/avatar-badge';
import {
  Room,
  RoomAbility,
  RoomCharacter,
  RoomMember,
  RoomError,
  RoomsApi,
  isNewerRoom,
  normalizeRoomCode,
  roomErrorMessage,
  toRoomError,
} from '../../rooms/rooms-api';
import { RoomLiveStatus, RoomLiveUpdates } from '../../rooms/room-live-updates';
import { LifeBoardView } from '../../games/chog-life/life-board';
import { LifeResultsView, toLifeResult } from '../../games/chog-life/life-results';
import { LIFE_CHOOSE_LANE, LIFE_ROLL, LifeRoute, USE_ABILITY, ordinal, toLifeBoard } from '../../games/chog-life/life-board-model';
import { ErrorState, LoadingState } from '../../shared/ui/state-views';
import { TurnTimer } from '../../rooms/turn-timer';
import { RaceBoardView } from '../../games/chog-race/race-board';
import { toRaceBoard } from '../../games/chog-race/race-board-model';

/** NFT Chogs have no colour of their own here; they use the brand colour. */
const NFT_COLOR = 'var(--brand)';

/**
 * A room the player is in and its lobby (STORY-021): code, invite link, members with their Chog, ability and ready
 * status, the player's ready toggle, the host's start and leaving (STORY-063). Once started it shows each player's locked
 * Chog, and leaving forfeits the game (STORY-023, DEC-025). In a game with abilities the player chooses one before
 * marking ready; others only see that a choice was made until the start locks it (STORY-033, DEC-035). It follows the room live (STORY-022, STORY-026): the
 * server pushes a versioned snapshot after every change and the page keeps the newest one it has seen, from the push or
 * a request alike, so an older answer never wins. A lost connection keeps retrying (STORY-027); Refresh is offered
 * while live updates are down. A started Chog Life game shows its board with every pawn (STORY-036, DEC-038), and the
 * player whose turn it is rolls from it (STORY-037, DEC-039).
 * The server decides who may start; the button state here is only a hint.
 */
@Component({
  selector: 'app-room',
  imports: [RouterLink, NgTemplateOutlet, LoadingState, ErrorState, AvatarBadge, LifeBoardView, LifeResultsView, RaceBoardView, TurnTimer],
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

  /**
   * The Chog Life board in the room's game state, once the game has started and after it was completed, with the final
   * portfolios (STORY-041); null for other games, no state or a game that ended early.
   */
  protected readonly lifeBoard = computed(() => {
    const room = this.room();
    return room?.gameType === 'chog-life' && (room.status === 'Active' || room.status === 'Completed') ? toLifeBoard(room.state) : null;
  });

  /** The Chog Race board in the room's game state, once the game has started (STORY-050, DEC-054); null otherwise. */
  protected readonly raceBoard = computed(() => {
    const room = this.room();
    return room?.gameType === 'chog-race' && (room.status === 'Active' || room.status === 'Completed') ? toRaceBoard(room.state) : null;
  });

  /**
   * A board is on the page: the live match you still play, or the final board. Its legend lists the players, so the
   * lobby's player list is hidden (DEC-048, DEC-054).
   */
  protected readonly boardShown = computed(() => {
    const room = this.room();
    return !!(this.lifeBoard() || this.raceBoard()) && (room?.status === 'Completed' || !this.you()?.hasLeft);
  });

  /**
   * You're playing a live Chog Life match: its title and details go beside the board (DEC-048). The Chog Race board
   * stays under the room's title.
   */
  protected readonly playing = computed(() => !!this.lifeBoard() && this.boardShown() && this.room()?.status === 'Active');

  /** A completed Chog Life match whose results the server stored (STORY-043, DEC-046), shown above the final board. */
  protected readonly hasLifeResults = computed(() => {
    const room = this.room();
    return room?.gameType === 'chog-life' && room.status === 'Completed' && room.members.some((member) => toLifeResult(member.result) !== null);
  });

  /** Whose turn it is and when it runs out, while a match is active (DEC-045). */
  protected readonly turnTimer = computed(() => {
    const room = this.room();
    const current = room?.members.find((member) => member.isTurn);
    if (room?.status !== 'Active' || !room.turnEndsAt || !room.turnSeconds || !current) {
      return null;
    }
    return { endsAt: room.turnEndsAt, seconds: room.turnSeconds, name: current.displayName, isYou: current.isYou };
  });

  /** The game has abilities: the player has some to choose from, or someone has one. */
  protected readonly hasAbilities = computed(() => {
    const room = this.room();
    return !!room && (room.abilities.length > 0 || room.members.some((member) => member.hasAbility));
  });

  /** Ready needs a chosen ability in a game with abilities; the server checks it too. */
  protected readonly needsAbility = computed(() => this.hasAbilities() && !this.you()?.hasAbility);

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

  protected chooseAbility(ability: RoomAbility): void {
    const room = this.room();
    if (room && this.you()?.ability?.id !== ability.id) {
      this.act(this.api.selectAbility(room.code, ability.id));
    }
  }

  /** What a player's card says about their ability. */
  protected abilityLabel(member: RoomMember): string {
    if (member.ability) {
      return member.ability.name;
    }
    return member.hasAbility ? 'Ability chosen' : 'Ability not chosen yet';
  }

  /** How often the locked ability can still be used in this game (DEC-036). */
  protected usesLeftLabel(usesLeft: number): string {
    if (usesLeft === 0) {
      return 'Already used.';
    }
    return usesLeft === 1 ? '1 use left.' : `${usesLeft} uses left.`;
  }

  /**
   * Rolls on the player's turn. The server rolls and moves; the new room arrives live, and is fetched as well in case live
   * updates are down. A refused roll (another player's turn, a turn that ran out, a stale view) shows the room as it is.
   */
  protected roll(): void {
    this.command(LIFE_ROLL);
  }

  /** Chooses the route to leave a fork by, after a roll stopped there (STORY-039, DEC-041); refusals as for a roll. */
  protected chooseLane(lane: LifeRoute): void {
    this.command(LIFE_CHOOSE_LANE, { lane });
  }

  /** Uses the player's locked ability on their turn (STORY-042, DEC-044); refusals as for a roll. */
  protected useAbility(abilityId: string): void {
    this.command(USE_ABILITY, { abilityId });
  }

  private command(type: string, payload: object = {}): void {
    const room = this.room();
    if (!room || this.busy()) {
      return;
    }
    this.busy.set(true);
    this.actionError.set(null);
    this.api.sendCommand(room, type, payload).subscribe({
      next: () => {
        this.busy.set(false);
        this.refreshQuietly();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.actionError.set(toRoomError(error));
        this.refreshQuietly();
      },
    });
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

  /** A final place as shown: 1st, 2nd, 3rd, 4th… (rooms hold at most six players). */
  protected readonly place = ordinal;

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
        if ([
            'players_not_ready',
            'character_unavailable',
            'ability_unavailable',
            'ability_required',
            'unknown_ability',
            'not_enough_players',
            'room_started',
            'room_closed',
          ].includes(roomError.code)) {
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
