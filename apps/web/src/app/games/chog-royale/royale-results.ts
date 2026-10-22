import { Component, computed, input } from '@angular/core';
import { AvatarBadge } from '../../profile/avatar-badge';
import { RoomMember } from '../../rooms/rooms-api';
import { ordinal } from '../chog-life/life-board-model';
import { orderedLines } from './royale-board';
import { EVENT_INFO, RoyaleBoard, signedMon } from './royale-board-model';

/**
 * Mirrors Chog Royale's result for one player (STORY-078, DEC-089), stored with their rank when the match ended: their
 * MON, its change from the start, the MON other players' cards took from them and their successful cards. The server
 * computes every value; the client only shows them.
 */
export interface RoyaleResult {
  mon: number;
  change: number;
  lost: number;
  successfulCards: number;
  /**
   * 20 points per Rug Pull or Whale Swap that landed, 10 per one that didn't and 10 per Chaos, at most 100 (STORY-096,
   * DEC-099); display only. Missing from results stored before STORY-096.
   */
  degenScore?: number;
  /** The version of the formula `degenScore` was computed with. */
  degenScoreVersion?: number;
}

/** Present together or not at all: a result from before STORY-096 has neither. */
const SCORE = ['degenScore', 'degenScoreVersion'] as const;

const isInt = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value);
const isCount = (value: unknown): value is number => isInt(value) && value >= 0;

/** The Chog Royale result in a member's `result`, or null when it isn't one this client understands. */
export function toRoyaleResult(value: unknown): RoyaleResult | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const result = value as Record<string, unknown>;
  const scored = SCORE.every((key) => isCount(result[key])) || SCORE.every((key) => result[key] === undefined);
  return scored && isInt(result['mon']) && result['mon'] >= 0 && isInt(result['change']) && isInt(result['lost']) && result['lost'] >= 0 && isInt(result['successfulCards']) && result['successfulCards'] >= 0
    ? (result as unknown as RoyaleResult)
    : null;
}

interface Standing {
  member: RoomMember;
  rank: number;
  result: RoyaleResult | null;
}

/** "Alice", "Alice and Bob", "Alice, Bob and Carol"; "You" for the viewer. */
function names(standings: Standing[]): string {
  const all = standings.map((s) => (s.member.isYou ? 'You' : s.member.displayName));
  return all.length > 1 ? `${all.slice(0, -1).join(', ')} and ${all[all.length - 1]}` : (all[0] ?? '');
}

/**
 * Chog Royale's results (STORY-082, ROYALE-049, ROYALE-051): the winners, every player in rank order with ties sharing a
 * place, their MON, its change, their Degen Score (STORY-096) and both tie-break values, then the last round's reveal and every round, as text. All of
 * it comes from the standings and the ledger the server stored, so every player sees the same after any reload. Players
 * who left are listed after the others, without results.
 */
@Component({
  selector: 'app-royale-results',
  imports: [AvatarBadge],
  templateUrl: './royale-results.html',
  styleUrls: ['../chog-life/life-results.scss', './royale-results.scss'],
})
export class RoyaleResultsView {
  readonly members = input.required<RoomMember[]>();
  /** The final view of the match, for its round-by-round history; null when it can't be read. */
  readonly board = input<RoyaleBoard | null>(null);

  protected readonly ordinal = ordinal;
  protected readonly signedMon = signedMon;
  protected readonly eventInfo = EVENT_INFO;

  protected readonly standings = computed<Standing[]>(() =>
    this.members()
      .flatMap((member) => (member.rank === null ? [] : [{ member, rank: member.rank, result: toRoyaleResult(member.result) }]))
      .sort((a, b) => a.rank - b.rank || (a.member.seat ?? 0) - (b.member.seat ?? 0)),
  );

  /** Whether any result has a Degen Score; a match from before STORY-096 has none. */
  protected readonly scored = computed(() => this.standings().some((s) => s.result?.degenScore !== undefined));

  protected readonly left = computed(() => this.members().filter((member) => member.rank === null && member.hasLeft));

  /** "Alice wins with 165 MON!", or "Alice and Bob share the win with 150 MON." */
  protected readonly winnerLine = computed(() => {
    const winners = this.standings().filter((s) => s.rank === 1);
    if (winners.length === 0) {
      return null;
    }
    const mon = winners[0].result ? ` with ${winners[0].result.mon} MON` : '';
    if (winners.length > 1) {
      return `${names(winners)} share the win${mon}.`;
    }
    return `${names(winners)} ${winners[0].member.isYou ? 'win' : 'wins'}${mon}!`;
  });

  /** Your seat, so your own lines stand out. */
  protected readonly yourSeat = computed(() => this.members().find((member) => member.isYou)?.seat ?? null);

  /** Every round with its lines in resolution order, the last one first: it is the reveal the match ended on. */
  protected readonly rounds = computed(() => {
    const board = this.board();
    const name = (seat: number) => {
      const member = this.members().find((m) => m.seat === seat);
      return member?.isYou ? 'You' : (member?.displayName ?? `Seat ${seat}`);
    };
    return (board?.ledger ?? []).map((round) => ({ round, lines: orderedLines(round, name) })).reverse();
  });
}
