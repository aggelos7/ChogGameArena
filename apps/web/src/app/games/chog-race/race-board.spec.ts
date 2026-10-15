import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomMember } from '../../rooms/rooms-api';
import { RACE_BOARD, RACE_TILES } from '../../testing';
import { seatColor } from '../seats';
import { RaceBoardView } from './race-board';
import { RaceBoard, raceColumns, raceLayout, raceTileLabel, toRaceBoard } from './race-board-model';

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
    result: null,
    seat,
    ...overrides,
  };
}

const NAMES = ['Alice', 'Bob', 'Carol', 'Dave', 'Erin', 'Frank'];

describe('Chog Race board model', () => {
  it('reads the board the server sends', () => {
    expect(toRaceBoard(RACE_BOARD)).toBe(RACE_BOARD);
  });

  it('refuses a state it cannot draw', () => {
    const tiles = RACE_TILES;
    for (const state of [
      null,
      'board',
      { rulesVersion: 1, tiles, pawns: 'none' },
      { rulesVersion: -1, tiles, pawns: [] },
      { rulesVersion: 1, tiles: [], pawns: [] },
      { rulesVersion: 1, tiles: [{ index: 1, type: 'Start' }], pawns: [] },
      { rulesVersion: 1, tiles: [{ index: 0, type: 'Branch', lane: 'Main' }], pawns: [] },
      { rulesVersion: 1, tiles: [{ index: 0, type: 'Boost' }], pawns: [] },
      { rulesVersion: 1, tiles: [{ index: 0, type: 'Setback', to: 5 }], pawns: [] },
      { rulesVersion: 1, tiles: [{ index: 0, type: 'Normal', to: 0 }], pawns: [] },
      { rulesVersion: 1, tiles, pawns: [{ seat: 1, tile: 28 }] },
      { rulesVersion: 1, tiles, pawns: [{ seat: 'one', tile: 0 }] },
    ]) {
      expect(toRaceBoard(state)).toBeNull();
    }
  });

  it('fits four tiles a row on a phone and up to ten on wider screens', () => {
    expect([0, 343, 500, 700, 1200].map(raceColumns)).toEqual([4, 4, 5, 8, 10]);
  });

  it('winds the track back and forth so consecutive tiles always touch', () => {
    const spots = raceLayout(RACE_TILES, 4);

    expect(spots.slice(0, 9).map((spot) => [spot.row, spot.column])).toEqual([
      [1, 1],
      [1, 2],
      [1, 3],
      [1, 4],
      [2, 4],
      [2, 3],
      [2, 2],
      [2, 1],
      [3, 1],
    ]);
    expect(spots[spots.length - 1]).toEqual({ tile: RACE_TILES[27], row: 7, column: 4 });
    for (let i = 1; i < spots.length; i++) {
      const [a, b] = [spots[i - 1], spots[i]];
      expect(Math.abs(a.row - b.row) + Math.abs(a.column - b.column)).toBe(1);
    }
  });

  it('labels each tile with what it does', () => {
    expect([0, 1, 2, 4, 9, 27].map((i) => raceTileLabel(RACE_TILES[i]))).toEqual(['Start', '', 'Card', 'Boost to 6', 'Back to 7', 'Finish']);
  });
});

describe('Chog Race board', () => {
  let fixture: ComponentFixture<RaceBoardView>;
  let page: HTMLElement;

  function render(board: RaceBoard, members: RoomMember[]): void {
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
    TestBed.configureTestingModule({ imports: [RaceBoardView] });
    fixture = TestBed.createComponent(RaceBoardView);
    page = fixture.nativeElement as HTMLElement;
  });

  it('shows every tile in order from start to finish, with what it does', () => {
    render(RACE_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true })]);

    const tiles = Array.from(page.querySelectorAll<HTMLElement>('[data-tile]'));
    expect(tiles.map((t) => Number(t.dataset['tile']))).toEqual(RACE_TILES.map((t) => t.index));
    expect(tile(0).textContent).toContain('Start');
    expect(tile(2).dataset['type']).toBe('Card');
    expect(tile(12).textContent).toContain('Boost to 15');
    expect(tile(16).textContent).toContain('Back to 13');
    expect(tile(27).textContent).toContain('Finish');
    expect(tile(16).getAttribute('aria-label')).toBe('Tile 16, Back to 13');
    expect(tile(1).getAttribute('aria-label')).toBe('Tile 1, Normal');
  });

  it('lays the track out four tiles a row on a phone, winding back and forth', () => {
    render(RACE_BOARD, [member('Alice', 1), member('Bob', 2)]);

    expect(page.querySelector('[data-columns]')?.getAttribute('data-columns')).toBe('4');
    expect([0, 3, 4, 7, 8].map((i) => [tile(i).style.gridRow, tile(i).style.gridColumn])).toEqual([
      ['1', '1'],
      ['1', '4'],
      ['2', '4'],
      ['2', '1'],
      ['3', '1'],
    ]);
  });

  it('draws all six pawns on one tile, numbered and coloured by seat', () => {
    const members = NAMES.map((name, i) => member(name, i + 1, { isTurn: i === 2, isYou: i === 4 }));
    render({ ...RACE_BOARD, pawns: [1, 2, 3, 4, 5, 6].map((seat) => ({ seat, tile: 0 })) }, members);

    expect(pawnsOn(0)).toEqual(['1', '2', '3', '4', '5', '6']);
    const pawns = Array.from(tile(0).querySelectorAll<HTMLElement>('[data-pawn]'));
    expect(pawns.map((p) => p.style.background)).toEqual([1, 2, 3, 4, 5, 6].map((seat) => hexToRgb(seatColor(seat))));
    expect(pawns.filter((p) => p.classList.contains('turn')).map((p) => p.dataset['pawn'])).toEqual(['3']);
    expect(pawns.filter((p) => p.classList.contains('you')).map((p) => p.dataset['pawn'])).toEqual(['5']);
    expect(tile(0).getAttribute('aria-label')).toBe('Tile 0, Start, Alice, Bob, Carol, Dave, Erin (you), Frank');
  });

  it('shows pawns where the server put them', () => {
    render(
      {
        ...RACE_BOARD,
        pawns: [
          { seat: 1, tile: 5 },
          { seat: 2, tile: 27 },
          { seat: 3, tile: 5 },
        ],
      },
      [member('Alice', 1), member('Bob', 2), member('Carol', 3)],
    );

    expect([pawnsOn(0), pawnsOn(5), pawnsOn(27)]).toEqual([[], ['1', '3'], ['2']]);
  });

  it('lists the players with their pawns and says whose turn it is', () => {
    render(RACE_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true }), member('Gone', 3, { hasLeft: true })]);

    const legend = Array.from(page.querySelectorAll<HTMLElement>('[data-list="pawns"] li'));
    expect(legend.map((li) => li.dataset['seat'])).toEqual(['1', '2']);
    expect(legend[0].textContent).toContain('Turn');
    expect(legend[1].textContent).toContain('You');
    expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s Alice’s turn.');

    render(RACE_BOARD, [member('Alice', 1), member('Bob', 2, { isYou: true, isTurn: true })]);
    expect(page.querySelector('[data-turn-line]')?.textContent?.trim()).toBe('It’s your turn.');
  });
});

/** How the DOM reports a `#rrggbb` colour set as a style. */
function hexToRgb(hex: string): string {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
  return `rgb(${r}, ${g}, ${b})`;
}
