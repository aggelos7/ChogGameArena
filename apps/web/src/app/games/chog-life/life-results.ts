import { Component, computed, input } from '@angular/core';
import { AvatarBadge } from '../../profile/avatar-badge';
import { RoomMember } from '../../rooms/rooms-api';
import { ordinal, signedMon } from './life-board-model';

/**
 * Mirrors Chog Life's result for one player (STORY-043, DEC-046), stored with their rank when the match ended. The server
 * computes every value; the client only shows them.
 */
export interface LifeResult {
  startingPortfolio: number;
  portfolio: number;
  liquid: number;
  assets: number;
  bonuses: number;
  liabilities: number;
  /** Their place in the order of arrival at the Finish; null if they didn't reach it. */
  place: number | null;
  airdrops: number;
  airdropMon: number;
  rugsSurvived: number;
  liquidations: number;
  biggestWin: number;
  degenRoutes: number;
  degenWins: number;
  /** How much risk they took, 0–100; it doesn't count towards the ranking. */
  degenScore: number;
  degenScoreVersion: number;
}

const COUNTS = [
  'liquid',
  'assets',
  'bonuses',
  'liabilities',
  'airdrops',
  'airdropMon',
  'rugsSurvived',
  'liquidations',
  'biggestWin',
  'degenRoutes',
  'degenWins',
  'degenScore',
  'degenScoreVersion',
] as const;

const isCount = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value >= 0;

/** The Chog Life result in a member's `result`, or null when it isn't one this client understands. */
export function toLifeResult(value: unknown): LifeResult | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const result = value as Record<string, unknown>;
  const valid =
    COUNTS.every((key) => isCount(result[key])) &&
    Number.isInteger(result['startingPortfolio']) &&
    Number.isInteger(result['portfolio']) &&
    (result['place'] === undefined || result['place'] === null || (isCount(result['place']) && result['place'] >= 1));
  return valid ? ({ ...result, place: result['place'] ?? null } as LifeResult) : null;
}

/** A member in the final standings, with their result if the game recorded one. */
interface Standing {
  member: RoomMember;
  rank: number;
  result: LifeResult | null;
}

/** "Alice", "Alice and Bob", "Alice, Bob and Carol"; "You" for the viewer. */
function names(standings: Standing[]): string {
  const all = standings.map((s) => (s.member.isYou ? 'You' : s.member.displayName));
  return all.length > 1 ? `${all.slice(0, -1).join(', ')} and ${all[all.length - 1]}` : (all[0] ?? '');
}

/**
 * Chog Life's results (STORY-043, LIFE-010, DEC-046): the winner, then every player in rank order with their starting
 * and final portfolio, where they reached the Finish, and their statistics: Degen Score, biggest win, airdrops, rugs
 * survived and liquidations. Everything comes from the results the server stored with the ranks, so every player sees
 * the same, after any reload. Players who left are listed after the others, without results.
 */
@Component({
  selector: 'app-life-results',
  imports: [AvatarBadge],
  templateUrl: './life-results.html',
  styleUrl: './life-results.scss',
})
export class LifeResultsView {
  readonly members = input.required<RoomMember[]>();

  protected readonly ordinal = ordinal;
  protected readonly signedMon = signedMon;

  protected readonly standings = computed<Standing[]>(() =>
    this.members()
      .flatMap((member) => (member.rank === null ? [] : [{ member, rank: member.rank, result: toLifeResult(member.result) }]))
      .sort((a, b) => a.rank - b.rank || (a.member.seat ?? 0) - (b.member.seat ?? 0)),
  );

  /** "Alice wins with 250 MON!", "You win…", or "Alice and Bob share the win…" for a tie. */
  protected readonly winnerLine = computed(() => {
    const winners = this.standings().filter((s) => s.rank === 1);
    if (winners.length === 0) {
      return null;
    }
    const verb = winners.length > 1 ? 'share the win' : winners[0].member.isYou ? 'win' : 'wins';
    const portfolio = winners[0].result?.portfolio;
    return `${names(winners)} ${verb}${portfolio === undefined ? '' : ` with ${portfolio} MON`}!`;
  });

}
