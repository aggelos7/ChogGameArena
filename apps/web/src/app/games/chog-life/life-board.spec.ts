import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomMember } from '../../rooms/rooms-api';
import { HOP_MS, LifeBoardView, SIDE_BY_SIDE, WIDE_BOARD } from './life-board';
import { FORK_BOARD, lifePawn } from '../../testing';
import {
  LifeBoard,
  LifeStakeReturns,
  LifeTile,
  SPOT_HEIGHT,
  SPOT_WIDTH,
  laneSummary,
  laneTiles,
  fittedLayout,
  lifeLayout,
  lifeRows,
  ordinal,
  pawnOffset,
  outcomeRule,
  previewSummary,
  seatColor,
  stakeSummary,
  timeLeft,
  toLifeBoard,
} from './life-board-model';

function member(displayName: string, seat: number, overrides: Partial<RoomMember> = {}): RoomMember {
  return {
    displayName,
    avatarId: 'classic',
    collector: null,
    avatarImageUrl: null,
    isHost: false,
    isYou: false,
    isReady: false,
    character: { kind: 'Standard', id: 'classic', name: 'Classic', color: '#5b2bd6' },
    ability: null,
    hasAbility: false,
    hasLeft: false,
    isTurn: false,
    rank: null,
    result: null,
    seat,
    ...overrides,
  };
}

const NAMES = ['Alice', 'Bob', 'Carol', 'Dave', 'Erin', 'Frank'];

/** A tile linking to `next`, or to `[safe, degen]` lanes for a branch. */
function t(index: number, type: LifeTile['type'], lane: LifeTile['lane'], next?: number | [number, number]): LifeTile {
  const links = Array.isArray(next) ? { safeNext: next[0], degenNext: next[1] } : next === undefined ? {} : { next };
  return { index, type, lane, ...links, ...(type === 'Reward' || type === 'Loss' ? { amount: 10 } : {}), ...(type === 'Event' ? { event: 'Market' } : {}) };
}

/** The shape of the server's version 1 board: 36 tiles, a 4-tile and a 3-tile fork. */
const FULL_TILES: LifeTile[] = [
  ...Array.from({ length: 9 }, (_, i) => t(i, i === 0 ? 'Start' : 'Normal', 'Main', i + 1)),
  t(9, 'Branch', 'Main', [10, 14]),
  t(10, 'Reward', 'Safe', 11),
  t(11, 'Normal', 'Safe', 12),
  t(12, 'Reward', 'Safe', 13),
  t(13, 'Normal', 'Safe', 18),
  t(14, 'Event', 'Degen', 15),
  t(15, 'Reward', 'Degen', 16),
  t(16, 'Event', 'Degen', 17),
  t(17, 'Event', 'Degen', 18),
  ...Array.from({ length: 7 }, (_, i) => t(18 + i, 'Normal', 'Main', 19 + i)),
  t(25, 'Branch', 'Main', [26, 29]),
  t(26, 'Reward', 'Safe', 27),
  t(27, 'Normal', 'Safe', 28),
  t(28, 'Reward', 'Safe', 32),
  t(29, 'Event', 'Degen', 30),
  t(30, 'Reward', 'Degen', 31),
  t(31, 'Event', 'Degen', 32),
  t(32, 'Event', 'Main', 33),
  t(33, 'Loss', 'Main', 34),
  t(34, 'Reward', 'Main', 35),
  t(35, 'Finish', 'Main'),
];
const FULL_BOARD: LifeBoard = { rulesVersion: 2, tiles: FULL_TILES, pawns: [lifePawn(1, 0), lifePawn(2, 9)] };

describe('Chog Life board model', () => {
  it('reads a board from the room state and refuses anything else', () => {
    expect(toLifeBoard(FORK_BOARD)).toEqual(FORK_BOARD);
    expect(toLifeBoard(null)).toBeNull();
    expect(toLifeBoard({ count: 3 })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, tiles: [] })).toBeNull();
    // Out of index order, a backward link, an unknown type and a pawn off the board.
    expect(toLifeBoard({ ...FORK_BOARD, tiles: [...FORK_BOARD.tiles].reverse() })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, tiles: FORK_BOARD.tiles.map((t) => (t.index === 4 ? { ...t, next: 1 } : t)) })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, tiles: FORK_BOARD.tiles.map((t) => (t.index === 1 ? { ...t, type: 'Teleport' } : t)) })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [lifePawn(1, 10, 100)] })).toBeNull();
    // A last move must name tiles on the board.
    const moved = { ...FORK_BOARD, lastMove: { seat: 1, roll: 3, from: 0, to: 2, path: [1, 2] } };
    expect(toLifeBoard(moved)).toEqual(moved);
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, to: 12 } })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, path: 'far' } })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, roll: -1 } })).toBeNull();
    // Every pawn carries a whole, non-negative MON balance, and a move's change is a whole number.
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 0 }] })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [lifePawn(1, 0, -5)] })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [lifePawn(1, 0, 1.5)] })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, change: -40 } })).not.toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, change: 'lots' } })).toBeNull();
    // A lane choice (STORY-039): only the safe or degen route, a seat choosing and a default route.
    const choosing = { ...FORK_BOARD, pawns: [lifePawn(1, 2, 100, { lane: 'Degen' })], choosing: 1, defaultLane: 'Safe' };
    expect(toLifeBoard(choosing)).toEqual(choosing);
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, lane: 'Safe', byDefault: true } })).not.toBeNull();
    expect(toLifeBoard({ ...choosing, pawns: [{ ...lifePawn(1, 2, 100), lane: 'Main' }] })).toBeNull();
    expect(toLifeBoard({ ...choosing, choosing: -1 })).toBeNull();
    expect(toLifeBoard({ ...choosing, defaultLane: 'Sideways' })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, lane: 'safe' } })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, byDefault: 'yes' } })).toBeNull();
    // A portfolio (STORY-040): whole parts, a total that may be negative, a stake on a route and stake returns.
    const staked = {
      ...FORK_BOARD,
      pawns: [lifePawn(1, 3, 0, { lane: 'Safe', stake: { lane: 'Safe', amount: 50 }, assets: 50, liabilities: 60 })],
      lastMove: { ...moved.lastMove, lane: 'Safe', staked: 50, matured: { lane: 'Degen', stake: 50, payout: 150 } },
      forkStake: 50,
      stakeReturns: [{ lane: 'Safe', outcomes: [{ chance: 100, percent: 125 }] }],
    };
    expect(toLifeBoard(staked)).toEqual(staked);
    expect(staked.pawns[0].portfolio).toBe(-10);
    for (const part of ['assets', 'bonuses', 'liabilities', 'portfolio']) {
      const missing: Record<string, unknown> = { ...lifePawn(1, 0) };
      delete missing[part];
      expect(toLifeBoard({ ...FORK_BOARD, pawns: [missing] })).toBeNull();
    }
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [lifePawn(1, 0, 100, { liabilities: -1 })] })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [{ ...lifePawn(1, 0), portfolio: 1.5 }] })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [{ ...lifePawn(1, 0), stake: { lane: 'Main', amount: 50 } }] })).toBeNull();
    expect(toLifeBoard({ ...staked, lastMove: { ...staked.lastMove, staked: -50 } })).toBeNull();
    expect(toLifeBoard({ ...staked, lastMove: { ...staked.lastMove, matured: { lane: 'Degen', stake: 50 } } })).toBeNull();
    expect(toLifeBoard({ ...staked, forkStake: 'fifty' })).toBeNull();
    expect(toLifeBoard({ ...staked, stakeReturns: [{ lane: 'Safe', outcomes: [] }] })).toBeNull();
    expect(toLifeBoard({ ...staked, stakeReturns: [{ lane: 'Safe', outcomes: [{ chance: 100 }] }] })).toBeNull();
  });

  it('reads finish places, the bonus and what ended the match, and refuses bad ones', () => {
    const finished = {
      ...FORK_BOARD,
      pawns: [lifePawn(1, 9, 100, { bonuses: 100, place: 1 }), lifePawn(2, 4)],
      lastMove: { seat: 1, roll: 2, from: 8, to: 9, path: [9], place: 1, bonus: 100 },
      finishBonus: 100,
      ended: 'Finish',
    };
    expect(toLifeBoard(finished)).toEqual(finished);
    expect(toLifeBoard({ ...finished, ended: 'TimeUp' })).not.toBeNull();
    expect(toLifeBoard({ ...finished, ended: 'Later' })).toBeNull();
    expect(toLifeBoard({ ...finished, finishBonus: -1 })).toBeNull();
    expect(toLifeBoard({ ...finished, pawns: [lifePawn(1, 9, 100, { place: 0 })] })).toBeNull();
    expect(toLifeBoard({ ...finished, lastMove: { ...finished.lastMove, place: 1.5 } })).toBeNull();
    expect(toLifeBoard({ ...finished, lastMove: { ...finished.lastMove, bonus: 'big' } })).toBeNull();
  });

  it('reads armed abilities, what they did and a preview, and refuses bad ones (STORY-042)', () => {
    const move = { seat: 1, roll: 3, from: 0, to: 2, path: [1, 2] };
    const withAbilities = {
      ...FORK_BOARD,
      pawns: [lifePawn(1, 2, 100, { armed: 'reward_boost' }), lifePawn(2, 0)],
      lastMove: { ...move, change: 20, boost: 5 },
      preview: { lane: 'Degen', roll: 3, to: 5, path: [5], outcome: { chance: 100, effect: 'Loss', amount: 0, percent: 40, minimum: 0 } },
      confirming: 1,
    };
    expect(toLifeBoard(withAbilities)).toEqual(withAbilities);
    expect(toLifeBoard({ ...withAbilities, lastMove: { ...move, change: -8, saved: 7 } })).not.toBeNull();
    expect(toLifeBoard({ ...withAbilities, lastMove: { ...move, change: 10, rerolled: -10 } })).not.toBeNull();
    expect(toLifeBoard({ ...withAbilities, pawns: [lifePawn(1, 2, 100, { armed: 3 as unknown as string })] })).toBeNull();
    expect(toLifeBoard({ ...withAbilities, lastMove: { ...move, boost: -1 } })).toBeNull();
    expect(toLifeBoard({ ...withAbilities, lastMove: { ...move, saved: 'half' } })).toBeNull();
    expect(toLifeBoard({ ...withAbilities, lastMove: { ...move, rerolled: 1.5 } })).toBeNull();
    expect(toLifeBoard({ ...withAbilities, lastMove: { ...move, lane: 'Safe', switched: true } })).not.toBeNull();
    expect(toLifeBoard({ ...withAbilities, lastMove: { ...move, switched: 'yes' } })).toBeNull();
    expect(toLifeBoard({ ...withAbilities, preview: 'soon' })).toBeNull();
    expect(toLifeBoard({ ...withAbilities, preview: { ...withAbilities.preview, to: 12 } })).toBeNull();
    expect(toLifeBoard({ ...withAbilities, preview: { ...withAbilities.preview, lane: 'Main' } })).toBeNull();
    expect(toLifeBoard({ ...withAbilities, preview: { ...withAbilities.preview, outcome: { effect: 'Moon' } } })).toBeNull();
    expect(toLifeBoard({ ...withAbilities, confirming: -1 })).toBeNull();
  });

  it('sums up a previewed move, with an event outcome as its rule', () => {
    const tiles = FORK_BOARD.tiles;
    const rug = { chance: 100, effect: 'Loss' as const, amount: 0, percent: 40, minimum: 0 };
    expect(previewSummary({ lane: 'Degen', roll: 1, to: 5, path: [5], outcome: rug }, tiles)).toBe(
      'Roll 1, land on tile 5 (Rug pull): lose 40% of your liquid MON',
    );
    expect(previewSummary({ lane: 'Safe', roll: 1, to: 3, path: [3] }, tiles)).toBe('Roll 1, land on tile 3 (+10 MON)');
    expect(previewSummary({ lane: 'Safe', roll: 2, to: 4, path: [3, 4] }, tiles)).toBe('Roll 2, land on tile 4');
    expect(previewSummary({ lane: 'Safe', roll: 4, to: 9, path: [3, 4, 8, 9] }, tiles)).toBe('Roll 4, reach the finish');
    expect(outcomeRule({ chance: 100, effect: 'Loss', amount: 0, percent: 25, minimum: 20 })).toBe('lose 25% of your liquid MON, at least 20 MON');
    expect(outcomeRule({ chance: 60, effect: 'Gain', amount: 10, percent: 0, minimum: 0 })).toBe('gain 10 MON');
    expect(outcomeRule({ chance: 50, effect: 'Gain', amount: 10, percent: 5, minimum: 0 })).toBe('gain 10 MON plus 5% of your liquid MON');
  });

  it('writes places and the time left', () => {
    expect([1, 2, 3, 4, 6].map(ordinal)).toEqual(['1st', '2nd', '3rd', '4th', '6th']);
    expect(timeLeft(1_200_000, 0)).toBe('20:00');
    expect(timeLeft(65_500, 0)).toBe('1:06');
    expect(timeLeft(5_000, 0)).toBe('0:05');
    expect(timeLeft(0, 3_000)).toBe('0:00');
  });

  it('sums up what a stake on a lane returns', () => {
    const safe: LifeStakeReturns = { lane: 'Safe', outcomes: [{ chance: 100, percent: 125 }] };
    const degen: LifeStakeReturns = { lane: 'Degen', outcomes: [{ chance: 30, percent: 300 }, { chance: 70, percent: 20 }] };

    expect(stakeSummary(50, safe)).toBe('Stake 50 MON, get 62 MON back');
    expect(stakeSummary(50, degen)).toBe('Stake 50 MON, get 150 MON back (30%) or 10 MON (70%)');
    expect(stakeSummary(7, safe)).toBe('Stake 7 MON, get 8 MON back');
    expect(stakeSummary(0, safe)).toBe('Nothing to stake');
    expect(stakeSummary(50, undefined)).toBe('Nothing to stake');
  });

  it('sums up what each lane of a fork holds', () => {
    expect(laneTiles(FORK_BOARD.tiles, 2, 'Safe').map((t) => t.index)).toEqual([3, 4]);
    expect(laneTiles(FORK_BOARD.tiles, 2, 'Degen').map((t) => t.index)).toEqual([5, 6, 7]);
    expect(laneTiles(FORK_BOARD.tiles, 1, 'Degen')).toEqual([]);
    expect(laneSummary(laneTiles(FORK_BOARD.tiles, 2, 'Safe'))).toBe('+10 MON');
    expect(laneSummary(laneTiles(FORK_BOARD.tiles, 2, 'Degen'))).toBe('Rug pull, +60 MON, Liquidation');
    expect(laneSummary([FORK_BOARD.tiles[4]])).toBe('Nothing happens');
  });

  it('lays a fork out as two lanes side by side that join the main track', () => {
    const rows = lifeRows(FORK_BOARD.tiles).map((row) =>
      row.kind === 'main' ? row.tile.index : row.kind === 'lanes' ? 'lanes' : [row.safe?.index ?? null, row.degen?.index ?? null],
    );

    expect(rows).toEqual([0, 1, 2, 'lanes', [3, 5], [4, 6], [null, 7], 8, 9]);
  });

  it('gives each of six seats its own colour', () => {
    expect(new Set([1, 2, 3, 4, 5, 6].map(seatColor)).size).toBe(6);
  });
});

describe('Chog Life illustrated board layout', () => {
  const at = (tiles: LifeTile[]) => {
    const layout = lifeLayout(tiles);
    return (index: number) => ({ x: layout?.spots[index].x, y: layout?.spots[index].y });
  };

  it('winds the main track in rows of four, turning back at the end of each row', () => {
    const tiles = Array.from({ length: 10 }, (_, i) => t(i, i === 0 ? 'Start' : i === 9 ? 'Finish' : 'Normal', 'Main', i < 9 ? i + 1 : undefined));
    const layout = lifeLayout(tiles);
    const spot = at(tiles);

    expect(tiles.map((_, i) => spot(i).x)).toEqual([54, 138, 222, 306, 306, 222, 138, 54, 54, 138]);
    expect(tiles.map((_, i) => spot(i).y)).toEqual([44, 44, 44, 44, 124, 124, 124, 124, 204, 204]);
    expect(layout?.height).toBe(248);
    const road = (from: number) => layout?.roads.find((r) => r.from === from)?.path;
    expect(road(0)).toBe('M54 44 L138 44');
    // U-turns bulge out past the side they turn at.
    expect(road(3)).toBe('M306 44 C354 44 354 124 306 124');
    expect(road(7)).toBe('M54 124 C6 124 6 204 54 204');
  });

  it('splits each fork into its two lanes side by side, which join the main track again', () => {
    const layout = lifeLayout(FORK_BOARD.tiles);
    const spot = at(FORK_BOARD.tiles);

    expect([spot(2), spot(3), spot(4)]).toEqual([
      { x: 222, y: 44 },
      { x: 110, y: 132 },
      { x: 110, y: 204 },
    ]);
    expect([spot(5), spot(6), spot(7)].map((s) => s.x)).toEqual([250, 250, 250]);
    expect([spot(5), spot(6), spot(7)].map((s) => s.y)).toEqual([132, 204, 276]);
    // After the fork the track starts a new row, still going the same way.
    expect([spot(8), spot(9)]).toEqual([
      { x: 54, y: 356 },
      { x: 138, y: 356 },
    ]);
    expect(layout?.laneNames).toEqual([
      { route: 'Safe', x: 110, y: 88 },
      { route: 'Degen', x: 250, y: 88 },
    ]);
    expect(layout?.roads.map((r) => `${r.from}-${r.to} ${r.lane}`)).toEqual([
      '0-1 Main',
      '1-2 Main',
      '2-3 Safe',
      '2-5 Degen',
      '3-4 Safe',
      '4-8 Safe',
      '5-6 Degen',
      '6-7 Degen',
      '7-8 Degen',
      '8-9 Main',
    ]);
    expect(layout?.roads.find((r) => r.from === 2 && r.to === 5)?.path).toBe('M222 44 C222 88 250 88 250 132');
    expect(layout?.roads.find((r) => r.from === 3)?.path).toBe('M110 132 L110 204');
  });

  it('turns the board on its side for wide screens, with each fork’s lane names between its lanes', () => {
    const tall = lifeLayout(FORK_BOARD.tiles);
    const wide = lifeLayout(FORK_BOARD.tiles, 'horizontal');

    expect([wide?.width, wide?.height]).toEqual([tall?.height, tall?.width]);
    expect(wide?.spots.map((s) => [s.x, s.y])).toEqual(tall?.spots.map((s) => [s.y, s.x]));
    expect(wide?.roads.find((r) => r.from === 2 && r.to === 5)?.path).toBe('M44 222 C88 222 88 250 132 250');
    // Safe runs above Degen; their names sit by each lane's first tile, between the two lanes.
    expect(wide?.laneNames).toEqual([
      { route: 'Safe', x: 132, y: 151 },
      { route: 'Degen', x: 132, y: 216 },
    ]);
  });

  it('fits the whole 36-tile board without tiles overlapping or leaving the board', () => {
    const layout = lifeLayout(FULL_TILES);
    expect(layout?.spots.map((s) => s.tile.index)).toEqual(FULL_TILES.map((tile) => tile.index));
    const spots = layout?.spots ?? [];
    for (const a of spots) {
      expect(a.x - SPOT_WIDTH / 2).toBeGreaterThanOrEqual(0);
      expect(a.x + SPOT_WIDTH / 2).toBeLessThanOrEqual(layout?.width ?? 0);
      expect(a.y + SPOT_HEIGHT / 2).toBeLessThan(layout?.height ?? 0);
      for (const b of spots.filter((other) => other !== a)) {
        expect(Math.abs(a.x - b.x) >= SPOT_WIDTH || Math.abs(a.y - b.y) >= SPOT_HEIGHT).toBe(true);
      }
    }
    // Every link of the graph is a road.
    expect(layout?.roads.length).toBe(37);
  });

  it('fits the horizontal board to the space it has: taller columns for a taller space', () => {
    const across = (columns: number) => lifeLayout(FULL_TILES, 'horizontal', columns);
    // Eight tiles to a column on the whole board, still without overlaps, and the lanes spread further apart.
    const eight = across(8);
    expect(eight?.height).toBe(696);
    const spots = eight?.spots ?? [];
    for (const a of spots) {
      for (const b of spots.filter((other) => other !== a)) {
        expect(Math.abs(a.x - b.x) >= SPOT_WIDTH || Math.abs(a.y - b.y) >= SPOT_HEIGHT).toBe(true);
      }
    }
    expect(new Set(spots.filter((s) => s.tile.lane !== 'Main').map((s) => s.y))).toEqual(new Set([201, 495]));

    // A wide, low space keeps four to a column; a full-height space beside the players takes more.
    expect(fittedLayout(FULL_TILES, 1044, 380)).toEqual(across(4));
    expect(fittedLayout(FULL_TILES, 1044, 804)).toEqual(across(8));
    // A wider space fills its height too, rather than keeping four to a column for bigger tiles.
    const filled = fittedLayout(FULL_TILES, 1304, 730);
    const scale = Math.min(1304 / (filled?.width ?? 1), 730 / (filled?.height ?? 1));
    expect(scale).toBeGreaterThanOrEqual(0.9);
    expect(scale * (filled?.height ?? 0)).toBeGreaterThan(650);
    // Too little room for readable tiles in any shape: the one drawn largest.
    expect(fittedLayout(FULL_TILES, 600, 300)).toEqual(across(4));
    expect(fittedLayout([t(0, 'Start', 'Main', 2), t(1, 'Normal', 'Main', 2), t(2, 'Finish', 'Main')], 1044, 804)).toBeNull();
  });

  it('gives up when a tile can be reached by no route, so the list is shown instead', () => {
    expect(lifeLayout([t(0, 'Start', 'Main', 2), t(1, 'Normal', 'Main', 2), t(2, 'Finish', 'Main')])).toBeNull();
  });

  it('spreads up to six pawns along the top and bottom edges of their tile', () => {
    const offsets = (count: number) => Array.from({ length: count }, (_, i) => pawnOffset(i, count));

    expect(offsets(1)).toEqual([{ x: 0, y: -27 }]);
    expect(offsets(2)).toEqual([
      { x: -12, y: -27 },
      { x: 12, y: -27 },
    ]);
    expect(offsets(4).map((o) => `${o.x},${o.y}`)).toEqual(['-20,-27', '0,-27', '20,-27', '0,27']);
    expect(offsets(6).map((o) => `${o.x},${o.y}`)).toEqual(['-20,-27', '0,-27', '20,-27', '-20,27', '0,27', '20,27']);
  });
});

describe('Chog Life board', () => {
  let fixture: ComponentFixture<LifeBoardView>;
  let page: HTMLElement;

  function render(board: LifeBoard, members: RoomMember[]): void {
    fixture.componentRef.setInput('board', board);
    fixture.componentRef.setInput('members', members);
    fixture.detectChanges();
  }

  function tile(index: number): HTMLElement {
    const found = page.querySelector<HTMLElement>(`[data-tile="${index}"]`);
    if (!found) {
      throw new Error(`No tile ${index} on the board.`);
    }
    return found;
  }
  const pawnsOn = (index: number) => Array.from(tile(index).querySelectorAll('[data-pawn]')).map((p) => p.textContent?.trim());

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [LifeBoardView] });
    fixture = TestBed.createComponent(LifeBoardView);
    page = fixture.nativeElement as HTMLElement;
  });

  it('shows every tile with its type and lane', () => {
    render(FORK_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true })]);

    const tiles = Array.from(page.querySelectorAll<HTMLElement>('[data-tile]'));
    expect(tiles.map((t) => Number(t.dataset['tile']))).toEqual([0, 1, 2, 3, 5, 4, 6, 7, 8, 9]);
    expect(tile(1).textContent).toContain('+20 MON');
    expect(tile(2).textContent).toContain('Safe or Degen?');
    expect(tile(5).textContent).toContain('Rug pull');
    expect(tile(8).textContent).toContain('−15 MON');
    expect(tile(9).textContent).toContain('Finish');
    expect([tile(3).dataset['lane'], tile(5).dataset['lane'], tile(8).dataset['lane']]).toEqual(['Safe', 'Degen', 'Main']);
    expect(page.querySelector('.row.lanes')?.textContent).toContain('Safe route');
    expect(tile(5).getAttribute('aria-label')).toBe('Tile 5, Rug pull, Degen route');
  });

  it.each([2, 6])('shows all %i pawns, numbered by seat, and whose turn it is', (count) => {
    const members = NAMES.slice(0, count).map((name, i) => member(name, i + 1, { isTurn: i === 1, isYou: i === 0 }));
    render({ ...FORK_BOARD, pawns: members.map((_, i) => lifePawn(i + 1, 0)) }, members);

    expect(pawnsOn(0)).toEqual(NAMES.slice(0, count).map((_, i) => String(i + 1)));
    expect(page.querySelectorAll('[data-pawn]').length).toBe(count);
    expect(Array.from(page.querySelectorAll('[data-list="pawns"] li')).map((li) => li.querySelector('.name')?.textContent)).toEqual(
      NAMES.slice(0, count),
    );
    expect(page.querySelector('[data-turn]')?.closest('li')?.dataset['seat']).toBe('2');
    expect(tile(0).querySelector('.pawn.turn')?.textContent?.trim()).toBe('2');
    expect(tile(0).querySelector('.pawn.you')?.textContent?.trim()).toBe('1');
    expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s Bob’s turn.');
    expect(tile(0).getAttribute('aria-label')).toContain('Alice (you), Bob');
  });

  it('draws a player’s profile photo as their pawn in the list and the legend (STORY-062)', () => {
    const photo = '/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10';
    render({ ...FORK_BOARD, pawns: [lifePawn(1, 0), lifePawn(2, 0)] }, [
      member('Alice', 1, { avatarImageUrl: photo, isTurn: true }),
      member('Bob', 2, { isYou: true }),
    ]);

    const [alice, bob] = Array.from(tile(0).querySelectorAll<HTMLElement>('[data-pawn]'));
    expect(alice.querySelector('img')?.getAttribute('src')).toBe(photo);
    expect(alice.classList).toContain('photo');
    expect(bob.querySelector('img')).toBeNull();
    expect(bob.textContent?.trim()).toBe('2');
    const legend = Array.from(page.querySelectorAll<HTMLElement>('[data-list="pawns"] [data-seat] .pawn'));
    expect(legend.map((p) => p.querySelector('img')?.getAttribute('src') ?? p.textContent?.trim())).toEqual([photo, '2']);
    // The pawns are the players' pictures in a match; no avatar badge beside them.
    expect(page.querySelector('app-avatar-badge')).toBeNull();
  });

  it('puts each pawn on its own tile and tells you when it is your turn', () => {
    render(
      { ...FORK_BOARD, pawns: [lifePawn(1, 6, 100), lifePawn(2, 3, 100), lifePawn(3, 9, 100)] },
      [member('Alice', 1, { isYou: true, isTurn: true }), member('Bob', 2), member('Carol', 3)],
    );

    expect([pawnsOn(6), pawnsOn(3), pawnsOn(9), pawnsOn(0)]).toEqual([['1'], ['2'], ['3'], []]);
    expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s your turn.');
  });

  it('only lists players who still have a pawn', () => {
    render({ ...FORK_BOARD, pawns: [lifePawn(1, 0, 100), lifePawn(3, 0, 100)] }, [
      member('Alice', 1, { isTurn: true }),
      member('Bob', 2, { hasLeft: true }),
      member('Carol', 3),
    ]);

    expect(Array.from(page.querySelectorAll('[data-list="pawns"] li')).map((li) => li.querySelector('.name')?.textContent)).toEqual([
      'Alice',
      'Carol',
    ]);
    expect(pawnsOn(0)).toEqual(['1', '3']);
  });

  it('offers Roll only on your turn and tells the page when it is pressed', () => {
    const rolls: number[] = [];
    fixture.componentInstance.roll.subscribe(() => rolls.push(1));
    render(FORK_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true })]);
    expect(page.querySelector('[data-roll]')).toBeNull();

    render(FORK_BOARD, [member('Alice', 1), member('Bob', 2, { isYou: true, isTurn: true })]);
    const roll = page.querySelector<HTMLButtonElement>('[data-roll]');
    expect(roll?.textContent?.trim()).toBe('Roll the die');
    roll?.click();
    expect(rolls).toEqual([1]);

    fixture.componentRef.setInput('rolling', true);
    fixture.detectChanges();
    expect(page.querySelector<HTMLButtonElement>('[data-roll]')?.disabled).toBe(true);
  });

  it('shows the last move and marks where it ended', () => {
    const members = [member('Alice', 1, { isYou: true }), member('Bob', 2, { isTurn: true })];
    render({ ...FORK_BOARD, pawns: [lifePawn(1, 2, 100), lifePawn(2, 0, 100)], lastMove: { seat: 1, roll: 2, from: 0, to: 2, path: [1, 2] } }, members);

    expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('You rolled 2 and moved to tile 2 (Safe or Degen?).');
    expect(Array.from(page.querySelectorAll('[data-track] .last-stop')).map((t) => (t as HTMLElement).dataset['tile'])).toEqual(['2']);
    expect(Array.from(page.querySelectorAll('[data-drawing] .last-stop')).map((t) => (t as HTMLElement).dataset['spot'])).toEqual(['2']);

    render({ ...FORK_BOARD, pawns: [lifePawn(1, 2, 100), lifePawn(2, 9, 100)], lastMove: { seat: 2, from: 9, to: 9, path: [] } }, members);
    expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('Bob passed at the finish.');
    expect(page.querySelector('.last-stop')).toBeNull();

    render(FORK_BOARD, members);
    expect(page.querySelector('[data-last-move]')).toBeNull();
  });

  describe('at a fork', () => {
    const atFork = (seat: number): LifeBoard => ({
      ...FORK_BOARD,
      pawns: [
        lifePawn(1, 2, 100),
        lifePawn(2, 0, 100),
      ].map((pawn) => (pawn.seat === seat ? { ...pawn, tile: 2 } : pawn)),
      lastMove: { seat, roll: 2, from: 0, to: 2, path: [1, 2] },
      choosing: seat,
      defaultLane: 'Safe',
    });
    const highlighted = () => Array.from(page.querySelectorAll<HTMLElement>('.tile.choice')).map((t) => Number(t.dataset['tile']));

    it('offers you both routes with what each lane holds, instead of Roll', () => {
      const chosen: string[] = [];
      fixture.componentInstance.chooseLane.subscribe((lane) => chosen.push(lane));
      render(atFork(1), [member('Alice', 1, { isYou: true, isTurn: true }), member('Bob', 2)]);

      expect(page.querySelector('[data-roll]')).toBeNull();
      expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('Choose your route.');
      const routes = Array.from(page.querySelectorAll<HTMLButtonElement>('[data-route]'));
      expect(routes.map((b) => [b.querySelector('.route-name')?.textContent, b.querySelector('.route-summary')?.textContent])).toEqual([
        ['Take the safe route', '+10 MON'],
        ['Take the degen route', 'Rug pull, +60 MON, Liquidation'],
      ]);
      expect(page.querySelector('[data-route-hint]')?.textContent).toBe('If time runs out, you take the safe route.');
      expect(highlighted().sort((a, b) => a - b)).toEqual([3, 4, 5, 6, 7]);
      expect(page.querySelector('[data-route-stake]')).toBeNull();

      routes[1].click();
      routes[0].click();
      expect(chosen).toEqual(['Degen', 'Safe']);

      fixture.componentRef.setInput('rolling', true);
      fixture.detectChanges();
      expect(Array.from(page.querySelectorAll<HTMLButtonElement>('[data-route]')).every((b) => b.disabled)).toBe(true);
    });

    it('says what staking on each route returns, with all your MON if you hold less than the stake', () => {
      const withStakes = (mon: number): LifeBoard => ({
        ...atFork(1),
        pawns: [lifePawn(1, 2, mon), lifePawn(2, 0)],
        forkStake: 50,
        stakeReturns: [
          { lane: 'Safe', outcomes: [{ chance: 100, percent: 125 }] },
          { lane: 'Degen', outcomes: [{ chance: 30, percent: 300 }, { chance: 70, percent: 20 }] },
        ],
      });
      const stakes = () => Array.from(page.querySelectorAll('[data-route-stake]')).map((s) => s.textContent?.trim());
      const members = [member('Alice', 1, { isYou: true, isTurn: true }), member('Bob', 2)];

      render(withStakes(100), members);
      expect(stakes()).toEqual(['Stake 50 MON, get 62 MON back', 'Stake 50 MON, get 150 MON back (30%) or 10 MON (70%)']);
      render(withStakes(20), members);
      expect(stakes()).toEqual(['Stake 20 MON, get 25 MON back', 'Stake 20 MON, get 60 MON back (30%) or 4 MON (70%)']);
      render(withStakes(0), members);
      expect(stakes()).toEqual(['Nothing to stake', 'Nothing to stake']);
    });

    it('tells everyone else who is choosing, without the choice buttons', () => {
      render(atFork(1), [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true })]);

      expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('Alice is choosing a route…');
      expect(page.querySelector('[data-routes]')).toBeNull();
      expect(page.querySelector('[data-roll]')).toBeNull();
      expect(highlighted().length).toBe(5);
    });

    it('ignores a choice left over once the turn has moved on', () => {
      render(atFork(1), [member('Alice', 1), member('Bob', 2, { isYou: true, isTurn: true })]);

      expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s your turn.');
      expect(page.querySelector('[data-roll]')).not.toBeNull();
      expect(highlighted()).toEqual([]);
    });

    it('says which route was taken, and when time chose it', () => {
      const members = [member('Alice', 1, { isYou: true }), member('Bob', 2, { isTurn: true })];
      const chosen = (seat: number, byDefault?: boolean): LifeBoard => ({
        ...FORK_BOARD,
        pawns: [lifePawn(seat, 2, 100, { lane: byDefault ? 'Safe' : 'Degen' })],
        lastMove: { seat, roll: 2, from: 0, to: 2, path: [1, 2], lane: byDefault ? 'Safe' : 'Degen', byDefault },
      });

      render(chosen(2), members);
      expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('Bob rolled 2 and moved to tile 2 (Safe or Degen?). Bob chose the degen route.');
      expect(tile(2).getAttribute('aria-label')).toBe('Tile 2, Safe or Degen?, Bob, taking the degen route');
      render(chosen(2, true), members);
      expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe(
        'Bob rolled 2 and moved to tile 2 (Safe or Degen?). Time ran out, so Bob takes the safe route.',
      );
      render(chosen(1, true), members);
      expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe(
        'You rolled 2 and moved to tile 2 (Safe or Degen?). Time ran out, so you take the safe route.',
      );
    });

    it('says what was staked on the route and what a stake paid back', () => {
      const members = [member('Alice', 1, { isYou: true }), member('Bob', 2, { isTurn: true })];
      const fork = { seat: 2, roll: 2, from: 0, to: 2, path: [1, 2] };
      const lastMove = (board: Partial<LifeBoard['lastMove']>, seat = 2) => {
        render({ ...FORK_BOARD, lastMove: { ...fork, seat, ...board } }, members);
        return page.querySelector('[data-last-move]')?.textContent?.trim();
      };

      expect(lastMove({ lane: 'Degen', staked: 50 })).toBe('Bob rolled 2 and moved to tile 2 (Safe or Degen?). Bob chose the degen route and staked 50 MON.');
      expect(lastMove({ lane: 'Safe', byDefault: true, staked: 20 })).toBe(
        'Bob rolled 2 and moved to tile 2 (Safe or Degen?). Time ran out, so Bob takes the safe route and stakes 20 MON.',
      );
      expect(lastMove({ lane: 'Safe', byDefault: true, staked: 50 }, 1)).toBe(
        'You rolled 2 and moved to tile 2 (Safe or Degen?). Time ran out, so you take the safe route and stake 50 MON.',
      );
      expect(lastMove({ from: 7, to: 8, path: [8], change: -15, matured: { lane: 'Degen', stake: 50, payout: 150 } })).toBe(
        'Bob rolled 2, landed on tile 8 (−15 MON) and lost 15 MON. Their degen stake of 50 MON paid back 150 MON.',
      );
      expect(lastMove({ from: 4, to: 9, path: [8, 9], matured: { lane: 'Safe', stake: 50, payout: 62 } }, 1)).toBe(
        'You rolled 2 and moved to tile 9 (Finish). Your safe stake of 50 MON paid back 62 MON.',
      );
    });
  });

  it('shows every player’s portfolio in the legend as the server values it', () => {
    // The total comes from the server, never added up here: Carol's is deliberately not the sum of her parts.
    render(
      {
        ...FORK_BOARD,
        pawns: [
          lifePawn(1, 3, 40, { lane: 'Safe', stake: { lane: 'Safe', amount: 50 }, assets: 50 }),
          lifePawn(2, 0, 0, { liabilities: 6 }),
          { ...lifePawn(3, 0, 100, { bonuses: 100 }), portfolio: 777 },
        ],
      },
      [member('Alice', 1, { isYou: true }), member('Bob', 2, { isTurn: true }), member('Carol', 3)],
    );

    const rows = Array.from(page.querySelectorAll('[data-list="pawns"] li'));
    expect(rows.map((li) => li.querySelector('[data-portfolio]')?.textContent?.trim())).toEqual(['90 MON', '-6 MON', '777 MON']);
    expect(rows.map((li) => li.querySelector('[data-breakdown]')?.textContent?.trim().replace(/\s+/g, ' '))).toEqual([
      'Liquid 40 · Assets 50 · Bonus 0 · Debt 0',
      'Liquid 0 · Assets 0 · Bonus 0 · Debt 6',
      'Liquid 100 · Assets 0 · Bonus 100 · Debt 0',
    ]);
    expect(rows[1].querySelector('[data-portfolio]')?.classList).toContain('negative');
  });

  it('says what the tile did to the mover’s MON', () => {
    const members = [member('Alice', 1, { isYou: true }), member('Bob', 2, { isTurn: true })];
    const moved = (change: number, seat = 1) => ({
      ...FORK_BOARD,
      lastMove: { seat, roll: 1, from: 0, to: 1, path: [1], change },
    });

    render(moved(20), members);
    expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('You rolled 1, landed on tile 1 (+20 MON) and won 20 MON.');
    render(moved(-40, 2), members);
    expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('Bob rolled 1, landed on tile 1 (+20 MON) and lost 40 MON.');
    render(moved(0, 2), members);
    expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('Bob rolled 1, landed on tile 1 (+20 MON) and their MON didn’t change.');
    render(moved(0), members);
    expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('You rolled 1, landed on tile 1 (+20 MON) and your MON didn’t change.');
  });

  describe('abilities', () => {
    const ability = (id: string, name: string, usesLeft = 1) => ({
      id,
      name,
      description: `${name} does its thing.`,
      target: 'None' as const,
      maxUses: 1,
      usesLeft,
    });
    const boost = ability('reward_boost', 'Reward Boost');
    const preview = ability('event_preview', 'Event Preview');
    const atFork: LifeBoard = { ...FORK_BOARD, pawns: [lifePawn(1, 2, 100), lifePawn(2, 0, 100)], choosing: 1, defaultLane: 'Safe' };
    const used = (): string[] => {
      const ids: string[] = [];
      fixture.componentInstance.useAbility.subscribe((id) => ids.push(id));
      return ids;
    };

    it('offers your ability beside Roll on your turn and tells the page its id', () => {
      const ids = used();
      render(FORK_BOARD, [member('Alice', 1, { isYou: true, isTurn: true, ability: boost }), member('Bob', 2)]);

      const button = page.querySelector<HTMLButtonElement>('[data-use-ability]');
      expect(button?.textContent).toContain('Use Reward Boost');
      expect(button?.textContent).toContain('Reward Boost does its thing.');
      button?.click();
      expect(ids).toEqual(['reward_boost']);
      expect(page.querySelector('[data-roll]')).not.toBeNull();

      fixture.componentRef.setInput('rolling', true);
      fixture.detectChanges();
      expect(page.querySelector<HTMLButtonElement>('[data-use-ability]')?.disabled).toBe(true);
    });

    it('does not offer an ability that is used up, armed, not yours to use now or for a finished player', () => {
      const offered = (board: LifeBoard, members: RoomMember[]) => {
        render(board, members);
        return page.querySelector('[data-use-ability]') !== null;
      };
      const bob = member('Bob', 2);

      expect(offered(FORK_BOARD, [member('Alice', 1, { isYou: true, isTurn: true, ability: ability('reward_boost', 'Reward Boost', 0) }), bob])).toBe(false);
      expect(offered(FORK_BOARD, [member('Alice', 1, { isYou: true, ability: boost }), member('Bob', 2, { isTurn: true })])).toBe(false);
      expect(offered({ ...FORK_BOARD, pawns: [lifePawn(1, 0, 100, { armed: 'reward_boost' }), lifePawn(2, 0)] }, [member('Alice', 1, { isYou: true, isTurn: true, ability: boost }), bob])).toBe(false);
      expect(offered(atFork, [member('Alice', 1, { isYou: true, isTurn: true, ability: boost }), bob])).toBe(false);
      expect(offered({ ...FORK_BOARD, pawns: [lifePawn(1, 9, 100, { place: 1 }), lifePawn(2, 0)] }, [member('Alice', 1, { isYou: true, isTurn: true, ability: boost }), bob])).toBe(false);
      expect(offered(FORK_BOARD, [member('Alice', 1, { isYou: true, isTurn: true, ability: preview }), bob])).toBe(false);
      expect(offered(FORK_BOARD, [member('Alice', 1, { isYou: true, isTurn: true }), bob])).toBe(false);
    });

    it('offers Event Preview at a fork before you choose, and says it is on once used', () => {
      const ids = used();
      const members = [member('Alice', 1, { isYou: true, isTurn: true, ability: preview }), member('Bob', 2)];
      render(atFork, members);
      expect(page.querySelector('[data-preview-armed]')).toBeNull();

      page.querySelector<HTMLButtonElement>('[data-routes] [data-use-ability]')?.click();
      expect(ids).toEqual(['event_preview']);

      render({ ...atFork, pawns: [lifePawn(1, 2, 100, { armed: 'event_preview' }), lifePawn(2, 0)] }, [
        member('Alice', 1, { isYou: true, isTurn: true, ability: { ...preview, usesLeft: 0 } }),
        member('Bob', 2),
      ]);
      expect(page.querySelector('[data-use-ability]')).toBeNull();
      expect(page.querySelector('[data-preview-armed]')?.textContent).toBe('Event Preview is on: choose a route to see your next move there.');
      expect(page.querySelectorAll('[data-route]').length).toBe(2);
    });

    const deciding: LifeBoard = {
      ...atFork,
      choosing: undefined,
      confirming: 1,
      pawns: [lifePawn(1, 2, 50, { lane: 'Degen', stake: { lane: 'Degen', amount: 50 }, assets: 50 }), lifePawn(2, 0)],
      lastMove: { seat: 1, roll: 2, from: 0, to: 2, path: [1, 2], lane: 'Degen', staked: 50 },
      preview: { lane: 'Degen', roll: 1, to: 5, path: [5], outcome: { chance: 100, effect: 'Loss', amount: 0, percent: 40, minimum: 0 } },
    };
    const usedUp = { ...preview, usesLeft: 0 };

    it('shows you your next move on the route you chose, then lets you keep it or switch', () => {
      const chosen: string[] = [];
      fixture.componentInstance.chooseLane.subscribe((lane) => chosen.push(lane));
      render(deciding, [member('Alice', 1, { isYou: true, isTurn: true, ability: usedUp }), member('Bob', 2)]);

      expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('Keep your route or switch?');
      expect(page.querySelector('[data-preview]')?.textContent?.trim()).toBe(
        'Your next move: Roll 1, land on tile 5 (Rug pull): lose 40% of your liquid MON.',
      );
      expect(page.querySelector('[data-keep] .route-name')?.textContent).toBe('Keep the degen route');
      expect(page.querySelector('[data-switch] .route-name')?.textContent).toBe('Switch to the safe route');
      expect(page.querySelector('[data-roll]')).toBeNull();
      expect(page.querySelector('[data-route]')).toBeNull();

      page.querySelector<HTMLButtonElement>('[data-switch]')?.click();
      page.querySelector<HTMLButtonElement>('[data-keep]')?.click();
      expect(chosen).toEqual(['Safe', 'Degen']);
    });

    it('tells everyone else who is deciding, without the preview', () => {
      render({ ...deciding, preview: undefined }, [member('Alice', 1, { isTurn: true, ability: usedUp }), member('Bob', 2, { isYou: true })]);

      expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('Alice is deciding whether to keep their route…');
      expect(page.querySelector('[data-keep-or-switch]')).toBeNull();
    });

    it('keeps showing your previewed move above Roll until you roll', () => {
      render({ ...deciding, confirming: undefined }, [member('Alice', 1, { isYou: true, isTurn: true, ability: usedUp }), member('Bob', 2)]);

      expect(page.querySelector('[data-preview]')?.textContent?.trim()).toBe('Previewed: Roll 1, land on tile 5 (Rug pull): lose 40% of your liquid MON.');
      expect(page.querySelector('[data-roll]')).not.toBeNull();
    });

    it('says when a route was switched after a preview', () => {
      render({ ...deciding, confirming: undefined, preview: undefined, lastMove: { seat: 1, roll: 2, from: 0, to: 2, path: [1, 2], lane: 'Safe', staked: 50, switched: true } }, [
        member('Alice', 1, { isTurn: true }),
        member('Bob', 2, { isYou: true }),
      ]);

      expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe(
        'Alice rolled 2 and moved to tile 2 (Safe or Degen?). Alice switched to the safe route, taking the 50 MON stake along.',
      );
    });

    it('marks an armed ability in the legend for everyone', () => {
      render({ ...FORK_BOARD, pawns: [lifePawn(1, 0), lifePawn(2, 0, 100, { armed: 'loss_protection' })] }, [
        member('Alice', 1, { isYou: true, isTurn: true }),
        member('Bob', 2, { ability: ability('loss_protection', 'Loss Protection', 0) }),
      ]);

      expect(Array.from(page.querySelectorAll('[data-armed]')).map((tag) => [tag.closest('li')?.dataset['seat'], tag.textContent?.trim()])).toEqual([
        ['2', 'Loss Protection armed'],
      ]);
    });

    it.each([
      [{ change: 20, boost: 5 }, 'You rolled 1, landed on tile 1 (+20 MON) and won 20 MON. Your Reward Boost added 5 MON to bonuses.'],
      [{ change: -8, saved: 7 }, 'You rolled 1, landed on tile 1 (+20 MON) and lost 8 MON. Your Loss Protection saved 7 MON.'],
      [{ change: 10, rerolled: -10 }, 'You rolled 1, landed on tile 1 (+20 MON) and won 10 MON. Your Lucky Reroll drew the event twice: it kept +10 MON over −10 MON.'],
      [{ change: 25, rerolled: 10 }, 'You rolled 1, landed on tile 1 (+20 MON) and won 25 MON. Your Lucky Reroll drew the event twice: it kept +25 MON over +10 MON.'],
    ])('says what an ability did to the move', (effect, text) => {
      render({ ...FORK_BOARD, lastMove: { seat: 1, roll: 1, from: 0, to: 1, path: [1], ...effect } }, [
        member('Alice', 1, { isYou: true }),
        member('Bob', 2, { isTurn: true }),
      ]);

      expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe(text);
    });
  });

  describe('at the finish', () => {
    afterEach(() => vi.useRealTimers());

    it('counts down the time left every second while the match runs', () => {
      // A board made under fake timers, so its clock ticks with them.
      fixture.destroy();
      vi.useFakeTimers({ now: new Date('2026-10-14T12:00:00Z') });
      fixture = TestBed.createComponent(LifeBoardView);
      page = fixture.nativeElement;
      fixture.componentRef.setInput('endsAt', '2026-10-14T12:20:00Z');
      render(FORK_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2)]);
      expect(page.querySelector('[data-clock]')?.textContent?.trim()).toBe('Time left 20:00');

      vi.advanceTimersByTime(61_000);
      fixture.detectChanges();
      expect(page.querySelector('[data-clock]')?.textContent?.trim()).toBe('Time left 18:59');
    });

    it('shows no clock without a time limit', () => {
      render(FORK_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2)]);

      expect(page.querySelector('[data-clock]')).toBeNull();
    });

    it('marks who finished in which place and says who won the bonus', () => {
      render(
        {
          ...FORK_BOARD,
          pawns: [lifePawn(1, 9, 100, { bonuses: 100, place: 1 }), lifePawn(2, 9, 80, { place: 2 }), lifePawn(3, 4)],
          lastMove: { seat: 2, roll: 2, from: 8, to: 9, path: [9], place: 2 },
          finishBonus: 100,
        },
        [member('Alice', 1), member('Bob', 2, { isYou: true }), member('Carol', 3, { isTurn: true })],
      );

      const finished = Array.from(page.querySelectorAll('[data-list="pawns"] li')).map((li) => li.querySelector('[data-finished]')?.textContent ?? null);
      expect(finished).toEqual(['Finished 1st', 'Finished 2nd', null]);
      expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('You rolled 2 and reached the finish in 2nd place.');

      render(
        { ...FORK_BOARD, pawns: [lifePawn(1, 9, 100, { bonuses: 100, place: 1 }), lifePawn(2, 4)], lastMove: { seat: 1, roll: 3, from: 8, to: 9, path: [9], place: 1, bonus: 100 } },
        [member('Alice', 1), member('Bob', 2, { isYou: true, isTurn: true })],
      );
      expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe(
        'Alice rolled 3 and reached the finish in 1st place, winning the 100 MON bonus.',
      );
    });

    it.each([
      ['Finish', 'Everyone reached the finish! Players are ranked by their final portfolio.'],
      ['TimeUp', 'Time’s up! Players are ranked by their final portfolio.'],
    ] as const)('says the match ended by %s and stops the clock', (ended, text) => {
      fixture.componentRef.setInput('endsAt', new Date(Date.now() + 60_000).toISOString());
      render({ ...FORK_BOARD, ended }, [member('Alice', 1, { rank: 1 }), member('Bob', 2, { isYou: true, rank: 1 })]);

      expect(page.querySelector('[data-ending]')?.textContent).toBe(text);
      expect(page.querySelector('[data-clock]')).toBeNull();
      expect(page.querySelector('[data-roll]')).toBeNull();
    });
  });
  describe('illustrated', () => {
    const boardPawn = (seat: number) => page.querySelector<SVGElement>(`[data-board-pawn="${seat}"]`);
    const at = (seat: number) => Number(boardPawn(seat)?.getAttribute('data-at'));

    afterEach(() => {
      vi.useRealTimers();
      vi.unstubAllGlobals();
    });

    /** A board made under fake timers, so the hops step with them. */
    function underFakeTimers(): void {
      fixture.destroy();
      vi.useFakeTimers();
      fixture = TestBed.createComponent(LifeBoardView);
      page = fixture.nativeElement;
    }

    it('draws every tile, both lanes of each fork and the roads, with the list behind it for screen readers', () => {
      render(FORK_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true })]);

      const drawing = page.querySelector('[data-drawing]');
      expect(drawing?.getAttribute('aria-hidden')).toBe('true');
      const spots = Array.from(page.querySelectorAll<SVGElement>('[data-spot]'));
      expect(spots.map((s) => Number(s.dataset['spot']))).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
      expect(spots.map((s) => s.dataset['lane'])).toEqual(['Main', 'Main', 'Main', 'Safe', 'Safe', 'Degen', 'Degen', 'Degen', 'Main', 'Main']);
      expect(spots[1].textContent).toContain('+20 MON');
      expect(spots[2].textContent).toContain('Fork');
      expect(spots[5].textContent).toContain('Rug pull');
      expect(Array.from(page.querySelectorAll('[data-road]')).map((r) => r.getAttribute('data-road'))).toContain('7-8');
      expect(Array.from(drawing?.querySelectorAll('.lane-title') ?? []).map((n) => n.textContent?.trim())).toEqual(['Safe', 'Degen']);
      // Each type and event kind has its own icon.
      const icon = (index: number) => spots[index].querySelector('.icon')?.innerHTML;
      expect(new Set([0, 1, 2, 4, 5, 7, 8, 9].map(icon)).size).toBe(8);
      expect(page.querySelector('[data-track]')?.classList).toContain('behind');
      expect(page.querySelectorAll('[data-tile]').length).toBe(10);
    });

    it('runs the board across a wide screen and down a narrow one', () => {
      let width = WIDE_BOARD;
      const observers: ((entries: { contentRect: { width: number } }[]) => void)[] = [];
      vi.stubGlobal(
        'ResizeObserver',
        class {
          constructor(private readonly callback: (entries: { contentRect: { width: number } }[]) => void) {
            observers.push(callback);
          }
          observe(): void {
            this.callback([{ contentRect: { width } }]);
          }
          disconnect(): void {
            // Nothing to stop.
          }
        },
      );
      fixture.destroy();
      fixture = TestBed.createComponent(LifeBoardView);
      page = fixture.nativeElement;
      render(FORK_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2)]);
      fixture.detectChanges();
      const viewBox = () => page.querySelector('[data-drawing]')?.getAttribute('viewBox');

      expect(viewBox()).toBe('0 0 400 360');
      width = WIDE_BOARD - 1;
      observers.forEach((callback) => callback([{ contentRect: { width } }]));
      fixture.detectChanges();
      expect(viewBox()).toBe('0 0 360 400');
    });

    it('beside the players, fills the height left on screen with the board', () => {
      vi.stubGlobal(
        'ResizeObserver',
        class {
          constructor(private readonly callback: (entries: { contentRect: { width: number } }[]) => void) {}
          observe(): void {
            this.callback([{ contentRect: { width: SIDE_BY_SIDE } }]);
          }
          disconnect(): void {
            // Nothing to stop.
          }
        },
      );
      vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ top: 100, width: 1000 } as DOMRect);
      fixture.destroy();
      fixture = TestBed.createComponent(LifeBoardView);
      page = fixture.nativeElement;
      render(FULL_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2)]);
      fixture.detectChanges();
      fixture.detectChanges();

      // jsdom's window is 768 pixels high: 652 are left below a board starting at 100, less a 16-pixel margin.
      const fitted = fittedLayout(FULL_TILES, 1000, 652);
      expect(page.querySelector('[data-drawing]')?.getAttribute('viewBox')).toBe(`0 0 ${fitted?.width} ${fitted?.height}`);
      const box = page.querySelector<HTMLElement>('.board-box');
      expect(box?.style.height).toBe(`${Math.min(652, (1000 * (fitted?.height ?? 0)) / (fitted?.width ?? 1))}px`);
      expect(page.querySelector('app-life-drawing')?.classList).toContain('fill');
      vi.restoreAllMocks();
    });

    it.each([2, 6])('draws all %i pawns where the server put them, ringing whose turn it is', (count) => {
      const members = NAMES.slice(0, count).map((name, i) => member(name, i + 1, { isTurn: i === 1, isYou: i === 0 }));
      render({ ...FORK_BOARD, pawns: members.map((_, i) => lifePawn(i + 1, i % 2 === 0 ? 0 : 5)) }, members);

      const pawns = Array.from(page.querySelectorAll<SVGElement>('[data-board-pawn]'));
      expect(pawns.map((p) => p.textContent?.trim())).toEqual(members.map((m) => String(m.seat)));
      expect(pawns.map((p) => Number(p.dataset['at']))).toEqual(members.map((_, i) => (i % 2 === 0 ? 0 : 5)));
      expect(page.querySelectorAll('[data-turn-ring]').length).toBe(1);
      expect(boardPawn(2)?.querySelector('[data-turn-ring]')).not.toBeNull();
      expect(boardPawn(1)?.querySelector('.pawn-body')?.classList).toContain('you');
      // Pawns sharing a tile stand apart.
      const places = pawns.map((p) => (p as SVGElement & { style: CSSStyleDeclaration }).style.transform);
      expect(new Set(places).size).toBe(count);
    });

    it('draws a player’s profile photo inside their seat-coloured pawn (STORY-062)', () => {
      const photo = '/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10';
      render({ ...FORK_BOARD, pawns: [lifePawn(1, 0), lifePawn(2, 0)] }, [
        member('Alice', 1, { avatarImageUrl: photo, isYou: true }),
        member('Bob', 2, { isTurn: true }),
      ]);

      expect(boardPawn(1)?.querySelector('image.pawn-photo')?.getAttribute('href')).toBe(photo);
      expect(boardPawn(1)?.querySelector('.pawn-body')?.getAttribute('fill')).toBe(seatColor(1));
      expect(boardPawn(1)?.querySelector('.pawn-seat')).toBeNull();
      expect(boardPawn(2)?.querySelector('image')).toBeNull();
      expect(boardPawn(2)?.textContent?.trim()).toBe('2');
    });

    it('switches to the list and back, and remembers the choice on this device', () => {
      render(FORK_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2)]);
      const toggle = () => page.querySelector<HTMLButtonElement>('[data-view-toggle]');

      expect(toggle()?.textContent?.trim()).toBe('Show as list');
      toggle()?.click();
      fixture.detectChanges();
      expect(page.querySelector('[data-drawing]')).toBeNull();
      expect(page.querySelector('[data-track]')?.classList).not.toContain('behind');
      expect(toggle()?.textContent?.trim()).toBe('Show illustrated board');

      fixture.destroy();
      fixture = TestBed.createComponent(LifeBoardView);
      page = fixture.nativeElement;
      render(FORK_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2)]);
      expect(page.querySelector('[data-drawing]')).toBeNull();

      toggle()?.click();
      fixture.detectChanges();
      expect(page.querySelector('[data-drawing]')).not.toBeNull();
      expect(localStorage.getItem('chogLife.boardView')).toBe('drawing');
    });

    it('shows only the list when the tile graph can’t be laid out', () => {
      const tiles = [t(0, 'Start', 'Main', 2), t(1, 'Normal', 'Main', 2), t(2, 'Finish', 'Main')];
      render({ ...FORK_BOARD, tiles, pawns: [lifePawn(1, 0)] }, [member('Alice', 1, { isTurn: true })]);

      expect(page.querySelector('[data-drawing]')).toBeNull();
      expect(page.querySelector('[data-view-toggle]')).toBeNull();
      expect(page.querySelector('[data-track]')?.classList).not.toContain('behind');
      expect(pawnsOn(0)).toEqual(['1']);
    });

    it('hops a moved pawn along the route the server resolved, a tile at a time, and never further', () => {
      underFakeTimers();
      const members = [member('Alice', 1, { isYou: true }), member('Bob', 2, { isTurn: true })];
      render({ ...FORK_BOARD, pawns: [lifePawn(1, 0), lifePawn(2, 0)] }, members);
      expect(at(1)).toBe(0);

      const moved: LifeBoard = { ...FORK_BOARD, pawns: [lifePawn(1, 2), lifePawn(2, 0)], lastMove: { seat: 1, roll: 2, from: 0, to: 2, path: [1, 2] } };
      render(moved, members);
      // The list always shows where the server put the pawn.
      expect(pawnsOn(2)).toEqual(['1']);
      const seen = [at(1)];
      for (let i = 0; i < 4; i++) {
        vi.advanceTimersByTime(HOP_MS);
        fixture.detectChanges();
        seen.push(at(1));
      }
      expect(seen).toEqual([0, 1, 2, 2, 2]);
      expect(at(2)).toBe(0);

      // The same move again, as when the page refetches after a command, doesn't hop again.
      render({ ...moved, choosing: 1 }, members);
      expect(at(1)).toBe(2);
    });

    it('shows a move without hops when the page loads, and with reduced motion', () => {
      underFakeTimers();
      const members = [member('Alice', 1, { isYou: true }), member('Bob', 2, { isTurn: true })];
      render({ ...FORK_BOARD, pawns: [lifePawn(1, 2), lifePawn(2, 0)], lastMove: { seat: 1, roll: 2, from: 0, to: 2, path: [1, 2] } }, members);
      expect(at(1)).toBe(2);

      vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(prefers-reduced-motion: reduce)' }));
      render({ ...FORK_BOARD, pawns: [lifePawn(1, 2), lifePawn(2, 5)], lastMove: { seat: 2, roll: 1, from: 2, to: 5, path: [5], lane: 'Degen' } }, members);
      expect(at(2)).toBe(5);
    });
  });
});
