import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomMember } from '../../rooms/rooms-api';
import { LifeBoardView } from './life-board';
import { FORK_BOARD } from '../../testing';
import { LifeBoard, laneSummary, laneTiles, lifeRows, seatColor, toLifeBoard } from './life-board-model';

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
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 10, mon: 100 }] })).toBeNull();
    // A last move must name tiles on the board.
    const moved = { ...FORK_BOARD, lastMove: { seat: 1, roll: 3, from: 0, to: 2, path: [1, 2] } };
    expect(toLifeBoard(moved)).toEqual(moved);
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, to: 12 } })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, path: 'far' } })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, roll: -1 } })).toBeNull();
    // Every pawn carries a whole, non-negative MON balance, and a move's change is a whole number.
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 0 }] })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 0, mon: -5 }] })).toBeNull();
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 0, mon: 1.5 }] })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, change: -40 } })).not.toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, change: 'lots' } })).toBeNull();
    // A lane choice (STORY-039): only the safe or degen route, a seat choosing and a default route.
    const choosing = { ...FORK_BOARD, pawns: [{ seat: 1, tile: 2, mon: 100, lane: 'Degen' }], choosing: 1, defaultLane: 'Safe' };
    expect(toLifeBoard(choosing)).toEqual(choosing);
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, lane: 'Safe', byDefault: true } })).not.toBeNull();
    expect(toLifeBoard({ ...choosing, pawns: [{ seat: 1, tile: 2, mon: 100, lane: 'Main' }] })).toBeNull();
    expect(toLifeBoard({ ...choosing, choosing: -1 })).toBeNull();
    expect(toLifeBoard({ ...choosing, defaultLane: 'Sideways' })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, lane: 'safe' } })).toBeNull();
    expect(toLifeBoard({ ...moved, lastMove: { ...moved.lastMove, byDefault: 'yes' } })).toBeNull();
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
    render({ ...FORK_BOARD, pawns: members.map((_, i) => ({ seat: i + 1, tile: 0, mon: 100 })) }, members);

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
      { ...FORK_BOARD, pawns: [{ seat: 1, tile: 6, mon: 100 }, { seat: 2, tile: 3, mon: 100 }, { seat: 3, tile: 9, mon: 100 }] },
      [member('Alice', 1, { isYou: true, isTurn: true }), member('Bob', 2), member('Carol', 3)],
    );

    expect([pawnsOn(6), pawnsOn(3), pawnsOn(9), pawnsOn(0)]).toEqual([['1'], ['2'], ['3'], []]);
    expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s your turn.');
  });

  it('only lists players who still have a pawn', () => {
    render({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 0, mon: 100 }, { seat: 3, tile: 0, mon: 100 }] }, [
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
    render({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 2, mon: 100 }, { seat: 2, tile: 0, mon: 100 }], lastMove: { seat: 1, roll: 2, from: 0, to: 2, path: [1, 2] } }, members);

    expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('You rolled 2 and moved to tile 2 (Safe or Degen?).');
    expect(Array.from(page.querySelectorAll('.last-stop')).map((t) => (t as HTMLElement).dataset['tile'])).toEqual(['2']);

    render({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 2, mon: 100 }, { seat: 2, tile: 9, mon: 100 }], lastMove: { seat: 2, from: 9, to: 9, path: [] } }, members);
    expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('Bob passed at the finish.');
    expect(page.querySelector('.last-stop')).toBeNull();

    render(FORK_BOARD, members);
    expect(page.querySelector('[data-last-move]')).toBeNull();
  });

  describe('at a fork', () => {
    const atFork = (seat: number): LifeBoard => ({
      ...FORK_BOARD,
      pawns: [
        { seat: 1, tile: 2, mon: 100 },
        { seat: 2, tile: 0, mon: 100 },
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

      routes[1].click();
      routes[0].click();
      expect(chosen).toEqual(['Degen', 'Safe']);

      fixture.componentRef.setInput('rolling', true);
      fixture.detectChanges();
      expect(Array.from(page.querySelectorAll<HTMLButtonElement>('[data-route]')).every((b) => b.disabled)).toBe(true);
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
        pawns: [{ seat, tile: 2, mon: 100, lane: byDefault ? 'Safe' : 'Degen' }],
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
  });

  it('shows every player’s MON in the legend', () => {
    render({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 1, mon: 120 }, { seat: 2, tile: 0, mon: 0 }] }, [
      member('Alice', 1, { isYou: true }),
      member('Bob', 2, { isTurn: true }),
    ]);

    expect(Array.from(page.querySelectorAll('[data-list="pawns"] li')).map((li) => li.querySelector('[data-mon]')?.textContent?.trim())).toEqual([
      '120 MON',
      '0 MON',
    ]);
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
