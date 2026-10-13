import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomMember } from '../../rooms/rooms-api';
import { LifeBoardView } from './life-board';
import { FORK_BOARD } from '../../testing';
import { LifeBoard, lifeRows, seatColor, toLifeBoard } from './life-board-model';

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
    expect(toLifeBoard({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 10 }] })).toBeNull();
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
    render({ ...FORK_BOARD, pawns: members.map((_, i) => ({ seat: i + 1, tile: 0 })) }, members);

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
      { ...FORK_BOARD, pawns: [{ seat: 1, tile: 6 }, { seat: 2, tile: 3 }, { seat: 3, tile: 9 }] },
      [member('Alice', 1, { isYou: true, isTurn: true }), member('Bob', 2), member('Carol', 3)],
    );

    expect([pawnsOn(6), pawnsOn(3), pawnsOn(9), pawnsOn(0)]).toEqual([['1'], ['2'], ['3'], []]);
    expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s your turn.');
  });

  it('only lists players who still have a pawn', () => {
    render({ ...FORK_BOARD, pawns: [{ seat: 1, tile: 0 }, { seat: 3, tile: 0 }] }, [
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
});
