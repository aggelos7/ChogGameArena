import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomMember } from '../../rooms/rooms-api';
import { royaleState } from '../../testing';
import { toRoyaleBoard } from './royale-board-model';
import { RoyaleResultsView, toRoyaleResult } from './royale-results';

function member(displayName: string, seat: number, rank: number | null, result: unknown, overrides: Partial<RoomMember> = {}): RoomMember {
  return {
    displayName,
    avatarId: 'classic',
    collector: null,
    avatarImageUrl: null,
    avatar: { id: 'classic', name: 'Classic', color: '#5b2bd6', imageUrl: null },
    isHost: false,
    isYou: false,
    isReady: false,
    character: { kind: 'Standard', id: 'classic', name: 'Classic', color: '#5b2bd6' },
    ability: null,
    hasAbility: true,
    hasLeft: false,
    isTurn: false,
    rank,
    seat,
    result,
    ...overrides,
  };
}

const result = (mon: number, lost = 0, successfulCards = 0) => ({ mon, change: mon - 100, lost, successfulCards });

describe('Chog Royale result model', () => {
  it('reads a result the server stored', () => {
    expect(toRoyaleResult(result(130, 15, 3))).toEqual({ mon: 130, change: 30, lost: 15, successfulCards: 3 });
  });

  it('reads a result with its Degen Score (STORY-096), and one stored before without it', () => {
    const scored = { ...result(130, 15, 3), degenScore: 40, degenScoreVersion: 1 };
    expect(toRoyaleResult(scored)).toEqual(scored);
    expect(toRoyaleResult(result(130))?.degenScore).toBeUndefined();
  });

  it('refuses anything else', () => {
    for (const bad of [
      null,
      'nope',
      { mon: 100 },
      { ...result(100), mon: -1 },
      { ...result(100), lost: '2' },
      { ...result(100), successfulCards: 1.5 },
      { ...result(100), degenScore: '10', degenScoreVersion: 1 },
      { ...result(100), degenScore: -10, degenScoreVersion: 1 },
      { ...result(100), degenScore: 10 },
    ]) {
      expect(toRoyaleResult(bad)).toBeNull();
    }
  });
});

describe('Chog Royale results', () => {
  let fixture: ComponentFixture<RoyaleResultsView>;
  let page: HTMLElement;

  function render(members: RoomMember[], state: Record<string, unknown> | null = null): void {
    TestBed.configureTestingModule({ imports: [RoyaleResultsView] });
    fixture = TestBed.createComponent(RoyaleResultsView);
    page = fixture.nativeElement as HTMLElement;
    fixture.componentRef.setInput('members', members);
    fixture.componentRef.setInput('board', state ? toRoyaleBoard(state) : null);
    fixture.detectChanges();
  }

  const lines = (selector: string) => Array.from(page.querySelectorAll(selector)).map((node) => node.textContent?.replace(/\s+/g, ' ').trim());

  it('lists the standings with MON, change and both tie-breaks, the server’s order and shared places', () => {
    render([
      member('Alice', 1, 1, result(140, 0, 2)),
      member('Bob', 2, 3, result(90, 20, 1), { isYou: true }),
      member('Carol', 3, 1, result(140, 0, 2)),
      member('Dan', 4, null, null, { hasLeft: true }),
    ]);

    expect(lines('[data-winner]')).toEqual(['Alice and Carol share the win with 140 MON.']);
    expect(lines('[data-place]')).toEqual(['1st', '1st', '3rd']);
    expect(lines('[data-mon]')).toEqual(['140 MON', '140 MON', '90 MON']);
    expect(lines('[data-summary]')).toEqual(['+40 MON since the start', '+40 MON since the start', '−10 MON since the start', 'Left the match']);
    expect(lines('[data-stat="lost"]')).toEqual(['0 MON', '0 MON', '20 MON']);
    expect(lines('[data-stat="successful-cards"]')).toEqual(['2', '2', '1']);
  });

  it('names you when you won alone', () => {
    render([member('Alice', 1, 2, result(80)), member('Bob', 2, 1, result(120), { isYou: true })]);

    expect(lines('[data-winner]')).toEqual(['You win with 120 MON!']);
  });

  it('shows every round as text, the last one first', () => {
    const final = royaleState({
      round: 2,
      ledger: [
        ...(royaleState()['ledger'] as object[]),
        {
          round: 2,
          event: 'MemeHype',
          chaos: 'CommunityAirdrop',
          priority: [2, 1],
          entries: [
            { seat: 1, card: 'Pump', byDefault: false, outcome: 'Applied', amount: 30, lost: 0, market: 5, change: 35, balance: 150 },
            { seat: 2, card: 'Chaos', byDefault: false, outcome: 'Applied', amount: 10, lost: 0, market: 5, change: 15, balance: 100 },
          ],
        },
      ],
    });

    render([member('Alice', 1, 1, result(150)), member('Bob', 2, 2, result(100), { isYou: true })], final);

    expect(lines('[data-history-round]')).toEqual(['Round 2 · Meme Hype + Community Airdrop', 'Round 1 · Calm Market']);
    expect(Array.from(page.querySelectorAll('[data-history-line]')).map((line) => line.classList.contains('yours'))).toEqual([true, false, false, true]);
    expect(lines('[data-history-line]').slice(0, 2)).toEqual([
      'You played Chaos and shook the market (+10 MON) and got 5 MON from the market: +15 MON, now 100 MON.',
      'Alice played Pump (+30 MON) and got 5 MON from the market: +35 MON, now 150 MON.',
    ]);
  });

  it('shows each player’s Degen Score first among their statistics, with a note that it doesn’t rank (STORY-096)', () => {
    render([
      member('Alice', 1, 1, { ...result(140, 0, 2), degenScore: 50, degenScoreVersion: 1 }),
      member('Bob', 2, 2, { ...result(60, 40, 0), degenScore: 0, degenScoreVersion: 1 }, { isYou: true }),
    ]);

    expect(lines('[data-stat="degen-score"]')).toEqual(['50/100', '0/100']);
    expect(page.querySelector('.stats div')?.classList).toContain('degen');
    expect(page.querySelector('.hint')?.textContent).toContain('The Degen Score counts the attacks and Chaos each player played: it doesn’t count towards the ranking.');
  });

  it('shows a match from before the Degen Score without one', () => {
    render([member('Alice', 1, 1, result(140, 0, 2)), member('Bob', 2, 2, result(60, 40, 0))]);

    expect(page.querySelector('[data-stat="degen-score"]')).toBeNull();
    expect(page.querySelector('.hint')?.textContent).not.toContain('Degen Score');
  });
});
