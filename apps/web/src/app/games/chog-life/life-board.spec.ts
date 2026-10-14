import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomMember } from '../../rooms/rooms-api';
import { LifeBoardView } from './life-board';
import { FORK_BOARD, lifePawn } from '../../testing';
import { LifeBoard, LifeStakeReturns, laneSummary, laneTiles, lifeRows, seatColor, stakeSummary, toLifeBoard } from './life-board-model';

function member(displayName: string, seat: number, overrides: Partial<RoomMember> = {}): RoomMember {
  return {
    displayName,
    avatarId: 'classic',
    isHost: false,
    isYou: false,
    isReady: false,
    character: { kind: 'Standard', id: 'classic', name: 'Classic', color: '#5b2bd6' },
    ability: null,
    hasAbility: false,
    hasLeft: false,
    isTurn: false,
    rank: null,
    seat,
    ...overrides,
  };
}

const NAMES = ['Alice', 'Bob', 'Carol', 'Dave', 'Erin', 'Frank'];

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
    expect(Array.from(page.querySelectorAll('.last-stop')).map((t) => (t as HTMLElement).dataset['tile'])).toEqual(['2']);

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
});
