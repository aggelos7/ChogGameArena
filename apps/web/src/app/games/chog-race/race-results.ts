import { Component, computed, input } from '@angular/core';
import { AvatarBadge } from '../../profile/avatar-badge';
import { RoomMember } from '../../rooms/rooms-api';
import { ordinal } from '../chog-life/life-board-model';

/**
 * Mirrors Chog Race's result for one player (STORY-056, DEC-060), stored with their rank when the match ended. The server
 * computes every value; the client only shows them.
 */
export interface RaceResult {
  /** The tile their pawn ended on. */
  tile: number;
  /** How many tiles it had left to the Finish. */
  tilesLeft: number;
  /** Whether it stood on the Finish. */
  finished: boolean;
  cardsPlayed: number;
  /** Their Rug, Liquidated or Whale that wasn't blocked, and Reverse that pushed someone back. */
  attacksLanded: number;
  /** Attacks on them they blocked with HODL or a Shield. */
  attacksBlocked: number;
  /**
   * 10 points per attack landed, at most 100 (STORY-066, DEC-062); display only. Missing from results stored before
   * STORY-066.
   */
  degenScore?: number;
  /** The version of the formula `degenScore` was computed with. */
  degenScoreVersion?: number;
}

const COUNTS = ['tile', 'tilesLeft', 'cardsPlayed', 'attacksLanded', 'attacksBlocked'] as const;

/** Present together or not at all: a result from before STORY-066 has neither. */
const SCORE = ['degenScore', 'degenScoreVersion'] as const;

const isCount = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value >= 0;

/** The Chog Race result in a member's `result`, or null when it isn't one this client understands. */
export function toRaceResult(value: unknown): RaceResult | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const result = value as Record<string, unknown>;
  const scored = SCORE.every((key) => isCount(result[key])) || SCORE.every((key) => result[key] === undefined);
  return COUNTS.every((key) => isCount(result[key])) && typeof result['finished'] === 'boolean' && scored
    ? (result as unknown as RaceResult)
    : null;
}

/** A member in the final standings, with their result if the game recorded one. */
interface Standing {
  member: RoomMember;
  rank: number;
  result: RaceResult | null;
}

/** "Alice", "Alice and Bob", "Alice, Bob and Carol"; "You" for the viewer. */
function names(standings: Standing[]): string {
  const all = standings.map((s) => (s.member.isYou ? 'You' : s.member.displayName));
  return all.length > 1 ? `${all.slice(0, -1).join(', ')} and ${all[all.length - 1]}` : (all[0] ?? '');
}

/**
 * Chog Race's results (STORY-056, RACE-008, RACE-009, DEC-060): the winner, then every player in rank order with where
 * their pawn ended and their statistics: Degen Score (STORY-066), cards played, attacks landed and attacks blocked. Everything comes from the
 * results the server stored with the ranks, so every player sees the same, after any reload. Players who left are listed
 * after the others, without results.
 */
@Component({
  selector: 'app-race-results',
  imports: [AvatarBadge],
  templateUrl: './race-results.html',
  // The same panel as Chog Life's results.
  styleUrl: '../chog-life/life-results.scss',
})
export class RaceResultsView {
  readonly members = input.required<RoomMember[]>();

  protected readonly ordinal = ordinal;

  protected readonly standings = computed<Standing[]>(() =>
    this.members()
      .flatMap((member) => (member.rank === null ? [] : [{ member, rank: member.rank, result: toRaceResult(member.result) }]))
      .sort((a, b) => a.rank - b.rank || (a.member.seat ?? 0) - (b.member.seat ?? 0)),
  );

  /** Whether any result has a Degen Score; a match from before STORY-066 has none. */
  protected readonly scored = computed(() => this.standings().some((s) => s.result?.degenScore !== undefined));

  /**
   * "Alice wins the race!" when a pawn reached the Finish; "Time's up! Alice got furthest." at the cap, or "Alice and Bob
   * share the win." when they were level.
   */
  protected readonly winnerLine = computed(() => {
    const winners = this.standings().filter((s) => s.rank === 1);
    if (winners.length === 0) {
      return null;
    }
    const you = winners.length === 1 && winners[0].member.isYou;
    if (winners[0].result?.finished) {
      return `${names(winners)} ${you ? 'win' : 'wins'} the race!`;
    }
    const verb = winners.length > 1 ? 'share the win' : 'got furthest';
    return `${winners[0].result ? 'Time’s up! ' : ''}${names(winners)} ${verb}.`;
  });

}
