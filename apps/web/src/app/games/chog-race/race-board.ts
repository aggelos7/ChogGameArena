import { Component, DestroyRef, ElementRef, afterNextRender, computed, inject, input, output, signal } from '@angular/core';
import { AvatarBadge } from '../../profile/avatar-badge';
import { Avatar } from '../../profile/profile-api';
import { RoomCharacter, RoomMember } from '../../rooms/rooms-api';
import { seatColor } from '../seats';
import {
  RACE_CARD_NAMES,
  RaceBoard,
  RaceCard,
  RaceDraw,
  RaceMove,
  RacePlay,
  RacePush,
  RaceTile,
  raceCardDescription,
  raceColumns,
  raceLayout,
  raceTileLabel,
} from './race-board-model';

/** NFT Chogs have no colour of their own here; they use the brand colour. */
const NFT_COLOR = 'var(--brand)';

/** A card you can play now, and for Whale, Rug and Liquidated the opponent it's played on (DEC-057, DEC-058). */
export interface RaceCardPlay {
  card: RaceCard;
  target?: number;
}

/** Where a pawn's own move ended and what the tile did, for a roll or a card's move alike. */
type Landing = Pick<RaceMove, 'landed' | 'to' | 'effect' | 'draw' | 'reshuffled'> & { card?: RaceCard };

/** A pawn as drawn: its seat, colour and the player it belongs to. */
interface DrawnPawn {
  seat: number;
  color: string;
  name: string;
  isYou: boolean;
  isTurn: boolean;
}

/**
 * The Chog Race board (STORY-050, DEC-054): the track as a serpentine grid, four tiles a row on a phone and up to ten on
 * wider screens, so the whole race is on screen. Each tile shows its number, what it does and the pawns on it, up to all
 * six. Pawns are numbered and coloured by seat, yours outlined and the current player's ringed, and the legend names
 * each player and whose turn it is. It only draws what the server sent. On your turn it offers Roll in the Move phase and
 * End turn in the Play phase (STORY-051, DEC-055), and says what the latest move did; the server decides every outcome.
 * It shows your hand, how many cards each player holds and what's left in the deck and discard pile (STORY-052, DEC-056);
 * the server only ever sends your own cards. In the Play phase it offers a button for each card you can play, Whale once
 * per opponent, and says what the card did (STORY-053, DEC-057). Rug and Liquidated are offered once per opponent they
 * can hurt; when one, or a Whale, is played on you while you hold cards, it asks you to block it with HODL or take it, and
 * everyone sees who it waits for and who skips a turn (STORY-054, DEC-058).
 */
@Component({
  selector: 'app-race-board',
  imports: [AvatarBadge],
  templateUrl: './race-board.html',
  styleUrl: './race-board.scss',
})
export class RaceBoardView {
  readonly board = input.required<RaceBoard>();
  readonly members = input.required<RoomMember[]>();
  /** A command is on its way: the buttons wait for it. */
  readonly rolling = input(false);
  readonly roll = output<void>();
  readonly endTurn = output<void>();
  readonly playCard = output<RaceCardPlay>();
  /** Lets an attack on you land (DEC-058). */
  readonly pass = output<void>();

  protected readonly label = raceTileLabel;

  /** The board's width, once measured; until then, and without ResizeObserver, the phone layout. */
  private readonly width = signal(0);

  protected readonly columns = computed(() => raceColumns(this.width()));
  protected readonly spots = computed(() => raceLayout(this.board().tiles, this.columns()));

  /** Players with a pawn on the board, by seat. */
  protected readonly players = computed(() => {
    const onBoard = new Set(this.board().pawns.map((pawn) => pawn.seat));
    return this.members()
      .filter((member): member is RoomMember & { seat: number } => member.seat !== null && onBoard.has(member.seat))
      .sort((a, b) => a.seat - b.seat);
  });

  /** The pawns standing on each tile, in seat order. */
  protected readonly pawnsByTile = computed(() => {
    const bySeat = new Map(this.players().map((member) => [member.seat, member]));
    const byTile = new Map<number, DrawnPawn[]>();
    for (const pawn of [...this.board().pawns].sort((a, b) => a.seat - b.seat)) {
      const member = bySeat.get(pawn.seat);
      const drawn: DrawnPawn = {
        seat: pawn.seat,
        color: seatColor(pawn.seat),
        name: member?.displayName ?? `Seat ${pawn.seat}`,
        isYou: !!member?.isYou,
        isTurn: !!member?.isTurn,
      };
      byTile.set(pawn.tile, [...(byTile.get(pawn.tile) ?? []), drawn]);
    }
    return byTile;
  });

  /** How many cards each seat holds. */
  protected readonly cardCounts = computed(() => new Map(this.board().pawns.map((pawn) => [pawn.seat, pawn.cards ?? 0])));

  /** Your cards by name, while you're playing; null when the server sent no hand. */
  /** Your cards by name with what each does, while you're playing; null when the server sent no hand. */
  protected readonly hand = computed(
    () =>
      this.board().hand?.map((card) => ({ name: this.cardName(card), description: raceCardDescription(card, this.board().cardValues) })) ??
      null,
  );

  protected readonly turnOf = computed(() => this.players().find((member) => member.isTurn) ?? null);

  /** Seats due to skip their next turn (DEC-058). */
  protected readonly skipping = computed(() => new Set(this.board().skipping ?? []));

  /**
   * The attack waiting for its target's answer, in the Reply phase (DEC-058), in words: what happened, whether it's you
   * who answers, and whether you hold a HODL to block it with.
   */
  protected readonly waitingReply = computed(() => {
    const board = this.board();
    const attack = board.reply;
    if (board.phase !== 'Reply' || !attack) {
      return null;
    }
    const target = this.members().find((member) => member.seat === attack.target);
    const card = this.cardName(attack.card);
    const isYou = !!target?.isYou;
    const canBlock = isYou && (board.hand ?? []).includes('Hodl');
    return {
      isYou,
      canBlock,
      text: isYou
        ? `${this.nameOf(attack.seat)} played ${card} on you. ${canBlock ? 'Block it with HODL or take it.' : 'You have no HODL to block it.'}`
        : `Waiting for ${this.nameOf(attack.target, false)} to answer ${this.possessive(attack.seat)} ${card}.`,
    };
  });

  /** Your turn's phase, while it's your turn in a live match; null otherwise. */
  protected readonly yourPhase = computed(() => (this.turnOf()?.isYou ? (this.board().phase ?? null) : null));

  /** The cards you can play now, in your Play phase, with what each does; Whale once per opponent. */
  protected readonly plays = computed(() => {
    if (this.yourPhase() !== 'Play') {
      return [];
    }
    const values = this.board().cardValues;
    const opponents = this.board().pawns.filter((pawn) => pawn.seat !== this.turnOf()?.seat);
    return (this.board().playable ?? []).flatMap((card): (RaceCardPlay & { label: string })[] => {
      const name = this.cardName(card);
      switch (card) {
        case 'Rug':
          // A pawn on the Start has nowhere to go back to.
          return opponents
            .filter((pawn) => pawn.tile > 0)
            .map((pawn) => ({
              card,
              target: pawn.seat,
              label: `${name}: ${this.nameOf(pawn.seat, false)} ${values ? `${values.rug} ` : ''}back`,
            }));
        case 'Liquidated':
          // Skips don't stack.
          return opponents
            .filter((pawn) => !this.skipping().has(pawn.seat))
            .map((pawn) => ({ card, target: pawn.seat, label: `${name}: ${this.nameOf(pawn.seat, false)} skips a turn` }));
        case 'Whale':
          return opponents.map((pawn) => ({
            card,
            target: pawn.seat,
            label: `${name}: swap with ${this.nameOf(pawn.seat, false)} (tile ${pawn.tile})`,
          }));
        case 'Pump':
          return [{ card, label: values ? `${name}: ${values.pump} forward` : name }];
        case 'Fomo':
          return [{ card, label: values ? `${name}: ${values.fomoForward} forward or ${values.fomoBack} back` : name }];
        case 'Airdrop':
          return [{ card, label: `${name}: roll again` }];
        case 'Reverse':
          return [{ card, label: values ? `${name}: others ${values.reverse} back` : name }];
        default:
          return [{ card, label: name }];
      }
    });
  });

  /** What the latest move did, in words: who rolled what, where they landed and where the tile sent them. */
  protected readonly lastMove = computed(() => {
    const move = this.board().lastMove;
    return move ? this.landed(`${this.nameOf(move.seat)} rolled ${move.roll}`, move, move.seat) : null;
  });

  /** What the card played after the latest roll did, in words (DEC-057). */
  protected readonly lastPlay = computed(() => {
    const move = this.board().lastMove;
    return move?.play ? this.played(move.play, move.seat) : null;
  });

  constructor() {
    const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    let resize: ResizeObserver | undefined;
    afterNextRender(() => {
      if (typeof ResizeObserver === 'function') {
        resize = new ResizeObserver(([entry]) => this.width.set(entry.contentRect.width));
        resize.observe(host);
      }
    });
    inject(DestroyRef).onDestroy(() => resize?.disconnect());
  }

  /** A seat's player: "You" (or "you" mid-sentence) for yours, otherwise their name. */
  private nameOf(seat: number, sentenceStart = true): string {
    const member = this.members().find((player) => player.seat === seat);
    return member?.isYou ? (sentenceStart ? 'You' : 'you') : (member?.displayName ?? `Seat ${seat}`);
  }

  /** A seat's player's, mid-sentence: "your" for yours, otherwise "Bob’s". */
  private possessive(seat: number): string {
    const name = this.nameOf(seat, false);
    return name === 'you' ? 'your' : `${name}’s`;
  }

  /** The sentence for a pawn's own move: `action` (who rolled or played what), where it landed and what the tile did. */
  private landed(action: string, landing: Landing, seat: number): string {
    const isYou = !!this.members().find((player) => player.seat === seat)?.isYou;
    const drew = landing.draw ? this.drew(landing.draw, landing.card, landing.reshuffled, isYou) : '';
    // A jump onto a Card tile draws there too: "…at tile 9, fell back to tile 7 and drew Rug."
    const then = drew ? ',' : ' and';
    switch (landing.effect) {
      case 'Boost':
        return `${action}, landed on the Boost at tile ${landing.landed}${then} jumped to tile ${landing.to}${drew}.`;
      case 'Setback':
        return `${action}, landed on the Setback at tile ${landing.landed}${then} fell back to tile ${landing.to}${drew}.`;
      case 'Card':
        return landing.draw
          ? `${action}, landed on the Card tile ${landing.to}${drew}.`
          : `${action} and landed on the Card tile ${landing.to}.`;
      case 'Finish':
        return this.board().winner === seat ? `${action}, reached the finish and won the race!` : `${action} and reached the finish.`;
      default:
        return `${action} and moved to tile ${landing.to}.`;
    }
  }

  /**
   * What a card play did: a move of the player's own, a swap, opponents pushed back or a skipped turn, and how the target
   * of an attack answered it (DEC-058).
   */
  private played(play: RacePlay, seat: number): string {
    const action = `${this.nameOf(seat)} played ${this.cardName(play.card)}`;
    if (play.target === undefined) {
      return this.effectOf(play, seat, action);
    }
    const target = this.nameOf(play.target, false);
    const attack = this.board().reply;
    if (play.reply === 'Blocked') {
      return `${action} on ${target}, blocked with HODL.`;
    }
    if (!play.reply && attack?.seat === seat && attack.target === play.target && this.board().phase === 'Reply') {
      return `${action} on ${target}, waiting for an answer.`;
    }
    const effect = this.effectOf(play, seat, action);
    return play.reply === 'TimedOut' ? `${effect} ${this.nameOf(play.target)} didn’t answer in time.` : effect;
  }

  /** What a card play did to the pawns, or to the turns. */
  private effectOf(play: RacePlay, seat: number, action: string): string {
    if (play.card === 'Liquidated' && play.target !== undefined) {
      const isYou = !!this.members().find((player) => player.seat === play.target)?.isYou;
      return `${action}: ${this.nameOf(play.target, false)} ${isYou ? 'skip your' : 'skips their'} next turn.`;
    }
    if (play.target !== undefined && play.to !== undefined) {
      const swapped = `${action} and swapped places with ${this.nameOf(play.target, false)} (now on tile ${play.from})`;
      return this.landed(swapped, { ...play, landed: play.to, to: play.to, card: play.drawn }, seat);
    }
    if (play.pushed) {
      return play.pushed.length > 0
        ? `${action}: ${play.pushed.map((push) => this.pushedTo(push)).join(', ')}.`
        : `${action}, but nobody could move back.`;
    }
    if (play.to === undefined || play.landed === undefined) {
      return `${action}.`;
    }
    const moved =
      play.roll !== undefined
        ? ` and rolled ${play.roll}`
        : play.steps !== undefined
          ? ` (${Math.abs(play.steps)} ${play.steps < 0 ? 'back' : 'forward'})`
          : '';
    return this.landed(`${action}${moved}`, { ...play, landed: play.landed, to: play.to, card: play.drawn }, seat);
  }

  /** Where Reverse sent an opponent: back a tile, then on with the Boost or Setback landed on. */
  private pushedTo(push: RacePush): string {
    const back = `${this.nameOf(push.seat, false)} back to tile ${push.landed}`;
    switch (push.effect) {
      case 'Boost':
        return `${back}, then boosted to tile ${push.to}`;
      case 'Setback':
        return `${back}, then set back to tile ${push.to}`;
      default:
        return back;
    }
  }

  /** What a Card tile drew, as the end of the last-move sentence: the card by name only for the player who drew it. */
  private drew(draw: RaceDraw, drawn: RaceCard | undefined, reshuffledFirst: boolean | undefined, isYou: boolean): string {
    switch (draw) {
      case 'Drawn': {
        const card = drawn ? this.cardName(drawn) : 'a card';
        const reshuffled = reshuffledFirst ? ' after the discard pile was shuffled into a new deck' : '';
        return ` and drew ${card}${reshuffled}`;
      }
      case 'HandFull':
        return `, but ${isYou ? 'your' : 'their'} hand is full`;
      case 'NoCards':
        return ', but no card was left to draw';
      default:
        return '';
    }
  }

  protected cardName(card: RaceCard): string {
    return RACE_CARD_NAMES[card];
  }

  protected cardsOf(seat: number): string {
    const count = this.cardCounts().get(seat) ?? 0;
    return count === 1 ? '1 card' : `${count} cards`;
  }

  protected pawnsOn(tile: RaceTile): DrawnPawn[] {
    return this.pawnsByTile().get(tile.index) ?? [];
  }

  protected color(seat: number): string {
    return seatColor(seat);
  }

  /** What a screen reader hears for a tile: its number, what it is and who stands on it. */
  protected describe(tile: RaceTile): string {
    const what = [`Tile ${tile.index}`, this.label(tile) || 'Normal'];
    const pawns = this.pawnsOn(tile);
    if (pawns.length > 0) {
      what.push(pawns.map((pawn) => (pawn.isYou ? `${pawn.name} (you)` : pawn.name)).join(', '));
    }
    return what.join(', ');
  }

  protected badge(character: RoomCharacter): Avatar {
    return { id: character.id, name: character.name, color: character.color ?? NFT_COLOR };
  }
}
