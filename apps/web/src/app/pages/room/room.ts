import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, Injector, OnInit, afterNextRender, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { GameCatalog } from '../../games/game-catalog';
import { Observable, Subject, Subscription, catchError, of, switchMap } from 'rxjs';
import { AvatarBadge } from '../../profile/avatar-badge';
import { CollectorChip } from '../../profile/collector-chip';
import {
  Room,
  RoomAbility,
  RoomMember,
  RoomError,
  RoomsApi,
  isNewerRoom,
  normalizeRoomCode,
  roomErrorMessage,
  serverClockOffset,
  toRoomError,
} from '../../rooms/rooms-api';
import { RoomLiveStatus, RoomLiveUpdates } from '../../rooms/room-live-updates';
import { ThemeMusic } from '../../shell/theme-music';
import { LifeBoardView } from '../../games/chog-life/life-board';
import { LifeResultsView, toLifeResult } from '../../games/chog-life/life-results';
import { RaceResultsView, toRaceResult } from '../../games/chog-race/race-results';
import { HolderBadge } from '../../games/holder-badge';
import { LIFE_CHOOSE_LANE, LIFE_ROLL, LifeRoute, USE_ABILITY, ordinal, toLifeBoard } from '../../games/chog-life/life-board-model';
import { ErrorState, LoadingState } from '../../shared/ui/state-views';
import { TurnTimer } from '../../rooms/turn-timer';
import { RaceAbilityPlay, RaceBoardView, RaceCardPlay } from '../../games/chog-race/race-board';
import { RACE_END_TURN, RACE_PASS, RACE_PLAY_CARD, RACE_ROLL, toRaceBoard } from '../../games/chog-race/race-board-model';
import { RoyaleAbilityUse, RoyaleBoardView, RoyaleSubmission } from '../../games/chog-royale/royale-board';
import { ROYALE_SUBMIT, toRoyaleBoard } from '../../games/chog-royale/royale-board-model';
import { RoyaleResultsView, toRoyaleResult } from '../../games/chog-royale/royale-results';

/** The longest a match's last move keeps the live board up before the results show anyway (DEC-076). */
export const FINALE_MOST_MS = 10_000;

/** How long a Chog Royale match's last round is revealed before the results (DEC-092). */
export const ROYALE_FINAL_REVEAL_MS = 10_000;

/**
 * A room the player is in and its lobby (STORY-021): code, invite link, members with their Chog, ability and ready
 * status, the player's ready toggle, the host's start and leaving (STORY-063). Once started it shows each player's locked
 * Chog, and leaving forfeits the game (STORY-023, DEC-025). In a game with abilities the player chooses one before
 * marking ready; others only see that a choice was made until the start locks it (STORY-033, DEC-035). It follows the room live (STORY-022, STORY-026): the
 * server pushes a versioned snapshot after every change and the page keeps the newest one it has seen, from the push or
 * a request alike, so an older answer never wins. A lost connection keeps retrying (STORY-027); Refresh is offered
 * while live updates are down. A started Chog Life game shows its board with every pawn (STORY-036, DEC-038), and the
 * player whose turn it is rolls from it (STORY-037, DEC-039). Chog Race rolls the same way and ends a turn in its Play
 * phase (STORY-051, DEC-055). Chog Royale is played in rounds on its own board (STORY-081): everyone decides at once
 * before a deadline counted on the server's clock (STORY-083), and the results show its standings and every round
 * (STORY-082).
 * The server decides who may start; the button state here is only a hint.
 */
@Component({
  selector: 'app-room',
  imports: [RouterLink, NgTemplateOutlet, LoadingState, ErrorState, AvatarBadge, CollectorChip, LifeBoardView, LifeResultsView, RaceBoardView, RaceResultsView, RoyaleBoardView, RoyaleResultsView, TurnTimer, HolderBadge],
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

  /** The Chog Royale board in the room's game state, once the game has started (STORY-081); null otherwise. */
  protected readonly royaleBoard = computed(() => {
    const room = this.room();
    return room?.gameType === 'chog-royale' && (room.status === 'Active' || room.status === 'Completed') ? toRoyaleBoard(room.state) : null;
  });

  /** How far the server's clock is ahead of this device's, from the newest snapshot (STORY-083, DEC-093). */
  protected readonly clockOffset = computed(() => serverClockOffset(this.room()));

  /**
   * A board is on the page: the live match you still play, or the final board. Its legend lists the players, so the
   * lobby's player list is hidden (DEC-048, DEC-054).
   */
  protected readonly boardShown = computed(() => {
    const room = this.room();
    return !!(this.lifeBoard() || this.raceBoard() || this.royaleBoard()) && (room?.status === 'Completed' || !this.you()?.hasLeft);
  });

  /**
   * The match you were playing just ended, and its last move is still being shown on the live board (DEC-076): the
   * die and the hops over the line come before the results, which wait until the board has settled.
   */
  protected readonly finale = signal(false);
  private finaleTimer: ReturnType<typeof setTimeout> | undefined;
  /** Until when (this device's clock) a Chog Royale match's last round is revealed before the results. */
  protected readonly finalRevealUntil = signal<number | null>(null);
  private readonly injector = inject(Injector);
  private readonly liveLifeBoard = viewChild(LifeBoardView);
  private readonly liveRaceBoard = viewChild(RaceBoardView);

  /** The room's status as the page shows it: still Active while the last move of a match you played is shown. */
  protected readonly shownStatus = computed(() => (this.finale() ? 'Active' : this.room()?.status));

  /** You're playing a live match: its title and details go beside the board, for Chog Life and Chog Race alike (DEC-048). */
  protected readonly playing = computed(
    () => !!(this.lifeBoard() || this.raceBoard() || this.royaleBoard()) && this.boardShown() && this.shownStatus() === 'Active',
  );

  /** A completed Chog Life match whose results the server stored (STORY-043, DEC-046), shown above the final board. */
  protected readonly hasLifeResults = computed(() => {
    const room = this.room();
    return room?.gameType === 'chog-life' && room.status === 'Completed' && room.members.some((member) => toLifeResult(member.result) !== null);
  });

  /** A completed Chog Race match whose results the server stored (STORY-056, DEC-060), shown above the final board. */
  protected readonly hasRaceResults = computed(() => {
    const room = this.room();
    return room?.gameType === 'chog-race' && room.status === 'Completed' && room.members.some((member) => toRaceResult(member.result) !== null);
  });

  /** A completed Chog Royale match whose results the server stored (STORY-078, STORY-082). */
  protected readonly hasRoyaleResults = computed(() => {
    const room = this.room();
    return room?.gameType === 'chog-royale' && room.status === 'Completed' && room.members.some((member) => toRoyaleResult(member.result) !== null);
  });

  /** Whose turn it is and when it runs out, while a match is active (DEC-045). */
  protected readonly turnTimer = computed(() => {
    const room = this.room();
    // While a Chog Race attack waits for its target's answer, the clock is theirs (DEC-058).
    const reply = this.raceBoard()?.phase === 'Reply' ? this.raceBoard()?.reply : undefined;
    const current = room?.members.find((member) => (reply ? member.seat === reply.target : member.isTurn));
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
    this.destroyRef.onDestroy(() => {
      this.liveSubscription?.unsubscribe();
      clearTimeout(this.finaleTimer);
    });
    // The theme pauses while you play a live match, and the game's own music plays instead (DEC-076).
    const music = inject(ThemeMusic);
    effect(() => music.setLiveGame(this.playing() ? (this.room()?.gameType ?? null) : null));
    this.destroyRef.onDestroy(() => music.setLiveGame(null));
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

  /**
   * The lobby's abilities in two groups (STORY-091, DEC-078): the standard ones everyone may choose, then the holder
   * abilities, those the player may choose and those shown locked because only Chog holders may choose them.
   */
  protected readonly abilityGroups = computed(() => {
    const room = this.room();
    const abilities = room?.abilities ?? [];
    return {
      standard: abilities.filter((ability) => !ability.holderOnly),
      holder: abilities.filter((ability) => ability.holderOnly),
      locked: room?.unavailableAbilities ?? [],
    };
  });

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
    this.command(this.raceBoard() ? RACE_ROLL : LIFE_ROLL);
  }

  /** Ends a Chog Race turn in its Play phase without playing a card (STORY-051, DEC-055); refusals as for a roll. */
  protected endTurn(): void {
    this.command(RACE_END_TURN);
  }

  /** Plays a Chog Race card in the Play phase, which ends the turn (STORY-053, DEC-057); refusals as for a roll. */
  protected playCard(play: RaceCardPlay): void {
    this.command(RACE_PLAY_CARD, play.target === undefined ? { card: play.card } : { card: play.card, target: play.target });
  }

  /** Lets a Chog Race attack on you land instead of blocking it (STORY-054, DEC-058); refusals as for a roll. */
  protected pass(): void {
    this.command(RACE_PASS);
  }

  /** Chooses the route to leave a fork by, after a roll stopped there (STORY-039, DEC-041); refusals as for a roll. */
  protected chooseLane(lane: LifeRoute): void {
    this.command(LIFE_CHOOSE_LANE, { lane });
  }

  /** Rolls keeping one of the dice Double Dice rolled, in either game (STORY-091, STORY-092); refusals as for a roll. */
  protected keepDie(die: number): void {
    this.command(this.raceBoard() ? RACE_ROLL : LIFE_ROLL, { die });
  }

  /** Uses the player's locked ability on their turn (STORY-042, DEC-044); refusals as for a roll. */
  protected useAbility(abilityId: string): void {
    this.command(USE_ABILITY, { abilityId });
  }

  /** Uses the player's locked Chog Race ability, on an opponent for Card Steal and Freeze (STORY-055, STORY-092); refusals as for a roll. */
  protected useRaceAbility(use: RaceAbilityPlay): void {
    this.command(USE_ABILITY, use.target === undefined ? { abilityId: use.abilityId } : { abilityId: use.abilityId, target: use.target });
  }

  /** Uses your locked Chog Royale ability while deciding, on an opponent for Insider Info (STORY-093); refusals as for a roll. */
  protected useRoyaleAbility(use: RoyaleAbilityUse): void {
    this.command(USE_ABILITY, use.target === undefined ? { abilityId: use.abilityId } : { abilityId: use.abilityId, target: use.target });
  }

  /** Sends your Chog Royale action for the round, saved or locked in (STORY-081, DEC-083); refusals as for a roll. */
  protected submitRoyale(submission: RoyaleSubmission): void {
    const { lock, ...action } = submission;
    this.command(ROYALE_SUBMIT, lock ? { ...action, lock } : action);
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
      // A match you're playing ends: keep the live board until it has shown the last move (or long enough for it).
      if (this.playing() && this.room()?.status === 'Active' && room.status === 'Completed' && room.gameType === 'chog-royale') {
        // Chog Royale's last round is revealed like any other before the results (DEC-092).
        this.finale.set(true);
        this.finalRevealUntil.set(Date.now() + ROYALE_FINAL_REVEAL_MS);
        clearTimeout(this.finaleTimer);
        this.finaleTimer = setTimeout(() => this.boardSettled(), ROYALE_FINAL_REVEAL_MS);
      } else if (this.playing() && this.room()?.status === 'Active' && room.status === 'Completed') {
        this.finale.set(true);
        clearTimeout(this.finaleTimer);
        this.finaleTimer = setTimeout(() => this.finale.set(false), FINALE_MOST_MS);
        // Once the board has taken the final state in: nothing to show if it isn't showing a move, else wait for it.
        afterNextRender(() => this.boardSettled(this.liveLifeBoard()?.moving() ?? this.liveRaceBoard()?.moving() ?? false), {
          injector: this.injector,
        });
      }
      this.room.set(room);
    }
  }

  /** The live board has shown its move; after a match's last one, the results take its place. */
  protected boardSettled(moving = false): void {
    if (this.finale() && !moving && this.room()?.status === 'Completed') {
      clearTimeout(this.finaleTimer);
      this.finale.set(false);
      this.finalRevealUntil.set(null);
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
