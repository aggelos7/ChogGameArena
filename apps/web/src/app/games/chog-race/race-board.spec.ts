import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomAbility, RoomMember } from '../../rooms/rooms-api';
import { RACE_BOARD, RACE_TILES } from '../../testing';
import { seatColor } from '../seats';
import { RaceBoardView } from './race-board';
import { RaceBoard, raceColumns, raceLayout, raceTileLabel, toRaceBoard } from './race-board-model';

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

describe('Chog Race board model', () => {
  it('reads the board the server sends', () => {
    expect(toRaceBoard(RACE_BOARD)).toBe(RACE_BOARD);
    const moved = { ...RACE_BOARD, lastMove: { seat: 1, roll: 2, from: 2, landed: 4, to: 6, effect: 'Boost' }, phase: 'Move', winner: 1 };
    expect(toRaceBoard(moved)).toBe(moved);
    for (const ended of ['Finish', 'TimeUp']) {
      const over = { ...RACE_BOARD, ended };
      expect(toRaceBoard(over)).toBe(over);
    }
    const drawn = {
      ...RACE_BOARD,
      hand: ['Pump', 'Hodl'],
      lastMove: { seat: 1, roll: 2, from: 0, landed: 2, to: 2, effect: 'Card', draw: 'Drawn', card: 'Hodl', reshuffled: true },
    };
    expect(toRaceBoard(drawn)).toBe(drawn);
    const played = {
      ...RACE_BOARD,
      playable: ['Pump', 'Whale'],
      cardValues: { pump: 3, fomoForward: 5, fomoBack: 3, fomoForwardChance: 50, reverse: 1, rug: 4 },
      lastMove: {
        seat: 1,
        roll: 1,
        from: 0,
        landed: 1,
        to: 1,
        play: { card: 'Fomo', steps: -3, from: 8, landed: 5, to: 5, effect: 'Card', draw: 'Drawn', drawn: 'Rug', reshuffled: true },
      },
    };
    expect(toRaceBoard(played)).toBe(played);
    const pushed = {
      ...RACE_BOARD,
      lastMove: { seat: 1, roll: 1, from: 0, landed: 1, to: 1, play: { card: 'Reverse', pushed: [{ seat: 2, from: 5, landed: 4, to: 6, effect: 'Boost' }] } },
    };
    expect(toRaceBoard(pushed)).toBe(pushed);
    const older = { rulesVersion: 1, tiles: RACE_TILES, pawns: [{ seat: 1, tile: 0 }] };
    expect(toRaceBoard(older)).toBe(older);
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
      { rulesVersion: 1, tiles, pawns: [], lastMove: { seat: 1, roll: 2, from: 0, landed: 2, to: 30 } },
      { rulesVersion: 1, tiles, pawns: [], lastMove: { seat: 1, roll: 2, from: 0, landed: 2, to: 2, effect: 'Branch' } },
      { rulesVersion: 1, tiles, pawns: [], lastMove: 'rolled' },
      { rulesVersion: 1, tiles, pawns: [], phase: 'React' },
      { rulesVersion: 1, tiles, pawns: [], winner: 'Alice' },
      { rulesVersion: 1, tiles, pawns: [], ended: 'Draw' },
      { rulesVersion: 1, tiles, pawns: [{ seat: 1, tile: 0, cards: -1 }] },
      { rulesVersion: 1, tiles, pawns: [], hand: ['Joker'] },
      { rulesVersion: 1, tiles, pawns: [], hand: 'Pump' },
      { rulesVersion: 1, tiles, pawns: [], deck: 'many' },
      { rulesVersion: 1, tiles, pawns: [], discard: -2 },
      { rulesVersion: 1, tiles, pawns: [], maxHand: 1.5 },
      { rulesVersion: 1, tiles, pawns: [], lastMove: { seat: 1, roll: 2, from: 0, landed: 2, to: 2, effect: 'Card', draw: 'Lost' } },
      { rulesVersion: 1, tiles, pawns: [], lastMove: { seat: 1, roll: 2, from: 0, landed: 2, to: 2, effect: 'Card', card: 'toString' } },
      { rulesVersion: 1, tiles, pawns: [], lastMove: { seat: 1, roll: 2, from: 0, landed: 2, to: 2, reshuffled: 'yes' } },
      { rulesVersion: 1, tiles, pawns: [], playable: ['Joker'] },
      { rulesVersion: 1, tiles, pawns: [], cardValues: { pump: 3 } },
      ...[
        'Pump',
        { card: 'Joker' },
        { card: 'Pump', steps: 1.5 },
        { card: 'Pump', to: 28 },
        { card: 'Pump', effect: 'Branch' },
        { card: 'Pump', drawn: 'Joker' },
        { card: 'Whale', target: -1 },
        { card: 'Reverse', pushed: [{ seat: 2, from: 5, landed: 4, to: 40 }] },
        { card: 'Reverse', pushed: [{ seat: 2, from: 5, to: 4 }] },
        { card: 'Reverse', pushed: [{ seat: 2, from: 5, landed: 4, to: 6, effect: 'Card' }] },
        { card: 'Reverse', pushed: 'all' },
      ].map((play) => ({ rulesVersion: 1, tiles, pawns: [], lastMove: { seat: 1, roll: 1, from: 0, landed: 1, to: 1, play } })),
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

  it('counts down to the time cap while the race runs, and stops once it is over (STORY-056)', () => {
    // The board's clock started a moment before this, which rounds up; 12:34 is any start within 0.6 seconds.
    fixture.componentRef.setInput('endsAt', new Date(Date.now() + 753_600).toISOString());
    render(RACE_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true })]);
    expect(page.querySelector('[data-clock]')?.textContent?.trim()).toBe('Time left 12:34');

    render({ ...RACE_BOARD, winner: 1, ended: 'Finish' }, [member('Alice', 1), member('Bob', 2, { isYou: true })]);
    expect(page.querySelector('[data-clock]')).toBeNull();
    render({ ...RACE_BOARD, ended: 'TimeUp' }, [member('Alice', 1), member('Bob', 2, { isYou: true })]);
    expect(page.querySelector('[data-clock]')).toBeNull();
  });

  it('shows no clock without a time cap', () => {
    render(RACE_BOARD, [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true })]);

    expect(page.querySelector('[data-clock]')).toBeNull();
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

  it('draws a player’s profile photo as their pawn, ringed in their seat colour (STORY-062)', () => {
    const photo = '/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10';
    render({ ...RACE_BOARD, pawns: [{ seat: 1, tile: 0 }, { seat: 2, tile: 0 }] }, [
      member('Alice', 1, { avatarImageUrl: photo }),
      member('Bob', 2, { isYou: true }),
    ]);

    const [alice, bob] = Array.from(tile(0).querySelectorAll<HTMLElement>('[data-pawn]'));
    expect(alice.querySelector('img')?.getAttribute('src')).toBe(photo);
    expect(alice.classList).toContain('photo');
    expect(alice.style.background).toBe(hexToRgb(seatColor(1)));
    expect(alice.textContent?.trim()).toBe('');
    expect(bob.querySelector('img')).toBeNull();
    expect(bob.textContent?.trim()).toBe('2');
    const legend = Array.from(page.querySelectorAll<HTMLElement>('[data-list="pawns"] [data-seat] .pawn'));
    expect(legend.map((p) => p.querySelector('img')?.getAttribute('src') ?? p.textContent?.trim())).toEqual([photo, '2']);
    expect(tile(0).getAttribute('aria-label')).toBe('Tile 0, Start, Alice, Bob (you)');
    // The pawns are the players' pictures in a match; no avatar badge beside them.
    expect(page.querySelector('app-avatar-badge')).toBeNull();
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

describe('Chog Race board moves (STORY-051)', () => {
  let fixture: ComponentFixture<RaceBoardView>;
  let page: HTMLElement;
  let events: string[];

  function render(board: RaceBoard, members: RoomMember[], rolling = false): void {
    fixture.componentRef.setInput('board', board);
    fixture.componentRef.setInput('members', members);
    fixture.componentRef.setInput('rolling', rolling);
    fixture.detectChanges();
  }

  const button = (selector: string) => page.querySelector<HTMLButtonElement>(selector);
  const lastMove = () => page.querySelector('[data-last-move]')?.textContent?.trim();
  const you = [member('Alice', 1, { isYou: true, isTurn: true }), member('Bob', 2)];

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [RaceBoardView] });
    fixture = TestBed.createComponent(RaceBoardView);
    page = fixture.nativeElement as HTMLElement;
    events = [];
    fixture.componentInstance.roll.subscribe(() => events.push('roll'));
    fixture.componentInstance.endTurn.subscribe(() => events.push('endTurn'));
  });

  it('offers Roll on your turn in the Move phase', () => {
    render({ ...RACE_BOARD, phase: 'Move' }, you);

    button('[data-roll]')?.click();

    expect(events).toEqual(['roll']);
    expect(button('[data-end-turn]')).toBeNull();
  });

  it('offers End turn on your turn in the Play phase', () => {
    render({ ...RACE_BOARD, phase: 'Play' }, you);

    button('[data-end-turn]')?.click();

    expect(events).toEqual(['endTurn']);
    expect(button('[data-roll]')).toBeNull();
  });

  it('offers nothing on another player’s turn, once the race is over, or while a command is on its way', () => {
    render({ ...RACE_BOARD, phase: 'Move' }, [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true })]);
    expect(button('[data-roll]')).toBeNull();

    render({ ...RACE_BOARD, winner: 1 }, you);
    expect(button('[data-roll]')).toBeNull();

    render({ ...RACE_BOARD, phase: 'Move' }, you, true);
    expect(button('[data-roll]')?.disabled).toBe(true);
  });

  it('says what the latest move did', () => {
    const moved = (lastMove: RaceBoard['lastMove'], extra: Partial<RaceBoard> = {}) => render({ ...RACE_BOARD, lastMove, ...extra }, you);

    moved({ seat: 2, roll: 3, from: 0, landed: 3, to: 3 });
    expect(lastMove()).toBe('Bob rolled 3 and moved to tile 3.');
    moved({ seat: 1, roll: 1, from: 3, landed: 4, to: 6, effect: 'Boost' });
    expect(lastMove()).toBe('You rolled 1, landed on the Boost at tile 4 and jumped to tile 6.');
    moved({ seat: 2, roll: 3, from: 6, landed: 9, to: 7, effect: 'Setback' });
    expect(lastMove()).toBe('Bob rolled 3, landed on the Setback at tile 9 and fell back to tile 7.');
    moved({ seat: 2, roll: 2, from: 0, landed: 2, to: 2, effect: 'Card' });
    expect(lastMove()).toBe('Bob rolled 2 and landed on the Card tile 2.');
    moved({ seat: 2, roll: 3, from: 25, landed: 27, to: 27, effect: 'Finish' }, { winner: 2 });
    expect(lastMove()).toBe('Bob rolled 3, reached the finish and won the race!');

    render(RACE_BOARD, you);
    expect(lastMove()).toBeUndefined();
  });
});

describe('Chog Race board cards (STORY-052)', () => {
  let fixture: ComponentFixture<RaceBoardView>;
  let page: HTMLElement;

  function render(board: RaceBoard, members: RoomMember[] = you): void {
    fixture.componentRef.setInput('board', board);
    fixture.componentRef.setInput('members', members);
    fixture.detectChanges();
  }

  const text = (selector: string) => page.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const all = (selector: string) => Array.from(page.querySelectorAll(selector)).map((node) => node.textContent?.trim());
  const you = [member('Alice', 1, { isYou: true, isTurn: true }), member('Bob', 2)];

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [RaceBoardView] });
    fixture = TestBed.createComponent(RaceBoardView);
    page = fixture.nativeElement as HTMLElement;
  });

  it('shows your cards by name, out of the most a hand holds', () => {
    render({ ...RACE_BOARD, hand: ['Pump', 'Hodl', 'Fomo'] });

    expect(text('[data-hand] h3')).toBe('Your cards 3/3');
    expect(all('[data-card] .card-name')).toEqual(['Pump', 'HODL', 'FOMO']);
    expect(page.querySelector('[data-hand] .empty')).toBeNull();
  });

  it('says what each card in your hand does, with the match’s values', () => {
    const cardValues = { pump: 3, fomoForward: 5, fomoBack: 3, fomoForwardChance: 50, reverse: 1, rug: 4 };
    render({ ...RACE_BOARD, cardValues, hand: ['Pump', 'Fomo', 'Airdrop', 'Whale', 'Reverse', 'Rug', 'Hodl', 'Liquidated'], maxHand: 8 });

    expect(all('[data-card] .card-text')).toEqual([
      'Move 3 forward.',
      '50% chance to move 5 forward, otherwise 3 back.',
      'Roll again.',
      'Swap places with an opponent you pick.',
      'Every opponent moves 1 back.',
      'Move an opponent 4 back.',
      'Block a Rug, Liquidated or Whale played on you.',
      'An opponent skips their next turn.',
    ]);

    render({ ...RACE_BOARD, hand: ['Pump', 'Reverse'] });
    expect(all('[data-card] .card-text')).toEqual(['Move forward.', 'Every opponent moves back.']);
  });

  it('says how to get a card while your hand is empty, and shows no hand to someone not playing', () => {
    render(RACE_BOARD);
    expect(text('[data-hand] h3')).toBe('Your cards 0/3');
    expect(text('[data-hand] .empty')).toBe('None yet. Land on a Card tile to draw one.');

    render({ ...RACE_BOARD, hand: undefined });
    expect(page.querySelector('[data-hand]')).toBeNull();
  });

  it('shows how many cards each player holds and what is left in the deck and discard pile', () => {
    render({
      ...RACE_BOARD,
      pawns: [
        { seat: 1, tile: 0, cards: 1 },
        { seat: 2, tile: 0, cards: 3 },
      ],
      deck: 20,
      discard: 0,
      hand: ['Rug'],
    });

    expect(all('[data-list="pawns"] [data-cards]')).toEqual(['1 card', '3 cards']);
    expect(text('[data-piles]')).toBe('Deck 20 · Discard 0');
  });

  it('says what a Card tile drew, naming the card only to the player who drew it', () => {
    const drew = (lastMove: RaceBoard['lastMove']) => {
      render({ ...RACE_BOARD, lastMove });
      return text('[data-last-move]');
    };
    const card = { roll: 2, from: 0, landed: 2, to: 2, effect: 'Card' } as const;

    expect(drew({ seat: 1, ...card, draw: 'Drawn', card: 'Hodl' })).toBe('You rolled 2, landed on the Card tile 2 and drew HODL.');
    expect(drew({ seat: 2, ...card, draw: 'Drawn' })).toBe('Bob rolled 2, landed on the Card tile 2 and drew a card.');
    expect(drew({ seat: 2, ...card, draw: 'Drawn', reshuffled: true })).toBe(
      'Bob rolled 2, landed on the Card tile 2 and drew a card after the discard pile was shuffled into a new deck.',
    );
    expect(drew({ seat: 1, ...card, draw: 'HandFull' })).toBe('You rolled 2, landed on the Card tile 2, but your hand is full.');
    expect(drew({ seat: 2, ...card, draw: 'HandFull' })).toBe('Bob rolled 2, landed on the Card tile 2, but their hand is full.');
    expect(drew({ seat: 2, ...card, draw: 'NoCards' })).toBe('Bob rolled 2, landed on the Card tile 2, but no card was left to draw.');
    expect(drew({ seat: 1, roll: 2, from: 7, landed: 9, to: 7, effect: 'Setback', draw: 'Drawn', card: 'Rug' })).toBe(
      'You rolled 2, landed on the Setback at tile 9, fell back to tile 7 and drew Rug.',
    );
    expect(drew({ seat: 2, roll: 2, from: 18, landed: 20, to: 22, effect: 'Boost', draw: 'HandFull' })).toBe(
      'Bob rolled 2, landed on the Boost at tile 20, jumped to tile 22, but their hand is full.',
    );
  });
});

describe('Chog Race board card plays (STORY-053)', () => {
  let fixture: ComponentFixture<RaceBoardView>;
  let page: HTMLElement;
  let plays: unknown[];

  const VALUES = { pump: 3, fomoForward: 5, fomoBack: 3, fomoForwardChance: 50, reverse: 1, rug: 4 };
  const you = [member('Alice', 1, { isYou: true, isTurn: true }), member('Bob', 2), member('Carol', 3)];
  const pawns = [
    { seat: 1, tile: 5, cards: 2 },
    { seat: 2, tile: 9, cards: 0 },
    { seat: 3, tile: 12, cards: 1 },
  ];

  function render(board: Partial<RaceBoard>, members: RoomMember[] = you, rolling = false): void {
    fixture.componentRef.setInput('board', { ...RACE_BOARD, pawns, cardValues: VALUES, ...board });
    fixture.componentRef.setInput('members', members);
    fixture.componentRef.setInput('rolling', rolling);
    fixture.detectChanges();
  }

  const buttons = () => Array.from(page.querySelectorAll<HTMLButtonElement>('[data-play]'));
  const labels = () => buttons().map((button) => button.textContent?.trim());
  const played = (lastMove: RaceBoard['lastMove'], members: RoomMember[] = you, winner?: number) => {
    render({ lastMove, winner }, members);
    return page.querySelector('[data-last-play]')?.textContent?.trim();
  };
  const roll = { roll: 1, from: 4, landed: 5, to: 5 };

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [RaceBoardView] });
    fixture = TestBed.createComponent(RaceBoardView);
    page = fixture.nativeElement as HTMLElement;
    plays = [];
    fixture.componentInstance.playCard.subscribe((play) => plays.push(play));
  });

  it('offers each card you can play in your Play phase, saying what it does, with End turn', () => {
    render({ phase: 'Play', hand: ['Pump', 'Fomo', 'Rug'], playable: ['Pump', 'Fomo', 'Airdrop', 'Reverse'] });

    expect(labels()).toEqual(['Pump: 3 forward', 'FOMO: 5 forward or 3 back', 'Airdrop: roll again', 'Reverse: others 1 back']);
    buttons()[1].click();
    expect(plays).toEqual([{ card: 'Fomo', target: undefined }]);
    expect(page.querySelector('[data-end-turn]')).not.toBeNull();
  });

  it('offers Whale once for each opponent, naming them and their tile', () => {
    render({ phase: 'Play', hand: ['Whale'], playable: ['Whale'] });

    expect(labels()).toEqual(['Whale: swap with Bob (tile 9)', 'Whale: swap with Carol (tile 12)']);
    buttons()[1].click();
    expect(plays).toEqual([{ card: 'Whale', target: 3 }]);
  });

  it('offers no card outside your Play phase or while a command is on its way', () => {
    render({ phase: 'Move', playable: ['Pump'] });
    expect(buttons()).toEqual([]);

    render({ phase: 'Play', playable: ['Pump'] }, [member('Alice', 1, { isTurn: true }), member('Bob', 2, { isYou: true })]);
    expect(buttons()).toEqual([]);

    render({ phase: 'Play', playable: [] });
    expect(buttons()).toEqual([]);
    expect(page.querySelector('[data-plays]')).toBeNull();

    render({ phase: 'Play', playable: ['Pump'] }, you, true);
    expect(buttons().map((button) => button.disabled)).toEqual([true]);
  });

  it('says what a card moving its player did, after the roll', () => {
    expect(played({ seat: 2, ...roll, play: { card: 'Pump', steps: 3, from: 5, landed: 8, to: 8 } })).toBe(
      'Bob played Pump (3 forward) and moved to tile 8.',
    );
    expect(played({ seat: 1, ...roll, play: { card: 'Fomo', steps: -3, from: 10, landed: 7, to: 7, effect: 'Card', draw: 'Drawn', drawn: 'Rug' } })).toBe(
      'You played FOMO (3 back), landed on the Card tile 7 and drew Rug.',
    );
    expect(played({ seat: 2, ...roll, play: { card: 'Airdrop', roll: 3, from: 6, landed: 9, to: 7, effect: 'Setback' } })).toBe(
      'Bob played Airdrop and rolled 3, landed on the Setback at tile 9 and fell back to tile 7.',
    );
    expect(played({ seat: 1, ...roll, play: { card: 'Pump', steps: 3, from: 6, landed: 9, to: 7, effect: 'Setback', draw: 'Drawn', drawn: 'Hodl' } })).toBe(
      'You played Pump (3 forward), landed on the Setback at tile 9, fell back to tile 7 and drew HODL.',
    );
    expect(played({ seat: 2, ...roll, play: { card: 'Pump', steps: 3, from: 25, landed: 27, to: 27, effect: 'Finish' } }, you, 2)).toBe(
      'Bob played Pump (3 forward), reached the finish and won the race!',
    );
    expect(page.querySelector('[data-last-move]')?.textContent?.trim()).toBe('Bob rolled 1 and moved to tile 5.');
  });

  it('says who Whale swapped with and who Reverse pushed back', () => {
    expect(played({ seat: 2, ...roll, play: { card: 'Whale', from: 5, to: 12, target: 1 } })).toBe(
      'Bob played Whale and swapped places with you (now on tile 5) and moved to tile 12.',
    );
    expect(played({ seat: 1, ...roll, play: { card: 'Whale', from: 5, to: 11, effect: 'Card', draw: 'Drawn', drawn: 'Rug', target: 2 } })).toBe(
      'You played Whale and swapped places with Bob (now on tile 5), landed on the Card tile 11 and drew Rug.',
    );
    expect(
      played({
        seat: 1,
        ...roll,
        play: {
          card: 'Reverse',
          pushed: [
            { seat: 2, from: 9, landed: 8, to: 8 },
            { seat: 3, from: 24, landed: 23, to: 21, effect: 'Setback' },
          ],
        },
      }),
    ).toBe('You played Reverse: Bob back to tile 8, Carol back to tile 23, then set back to tile 21.');
    expect(played({ seat: 2, ...roll, play: { card: 'Reverse', pushed: [{ seat: 1, from: 5, landed: 4, to: 6, effect: 'Boost' }] } })).toBe(
      'Bob played Reverse: you back to tile 4, then boosted to tile 6.',
    );
    expect(played({ seat: 3, ...roll, play: { card: 'Reverse', pushed: [] } })).toBe('Carol played Reverse, but nobody could move back.');
    expect(played({ seat: 3, ...roll })).toBeUndefined();
  });
});

describe('Chog Race board attacks and defense (STORY-054)', () => {
  let fixture: ComponentFixture<RaceBoardView>;
  let page: HTMLElement;
  let plays: unknown[];
  let passes: number;

  const VALUES = { pump: 3, fomoForward: 5, fomoBack: 3, fomoForwardChance: 50, reverse: 1, rug: 4 };
  const pawns = [
    { seat: 1, tile: 5, cards: 2 },
    { seat: 2, tile: 0, cards: 1 },
    { seat: 3, tile: 12, cards: 1 },
  ];
  const yourTurn = [member('Alice', 1, { isYou: true, isTurn: true }), member('Bob', 2), member('Carol', 3)];
  const bobsTurn = [member('Alice', 1, { isYou: true }), member('Bob', 2, { isTurn: true }), member('Carol', 3)];
  const roll = { roll: 1, from: 4, landed: 5, to: 5 };

  function render(board: Partial<RaceBoard>, members: RoomMember[] = yourTurn, rolling = false): void {
    fixture.componentRef.setInput('board', { ...RACE_BOARD, pawns, cardValues: VALUES, ...board });
    fixture.componentRef.setInput('members', members);
    fixture.componentRef.setInput('rolling', rolling);
    fixture.detectChanges();
  }

  const text = (selector: string) => page.querySelector(selector)?.textContent?.trim();
  const labels = () => Array.from(page.querySelectorAll('[data-play]')).map((button) => button.textContent?.trim());

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [RaceBoardView] });
    fixture = TestBed.createComponent(RaceBoardView);
    page = fixture.nativeElement as HTMLElement;
    plays = [];
    passes = 0;
    fixture.componentInstance.playCard.subscribe((play) => plays.push(play));
    fixture.componentInstance.pass.subscribe(() => passes++);
  });

  it('reads an attack waiting for its answer and who skips a turn', () => {
    const waiting = {
      ...RACE_BOARD,
      phase: 'Reply',
      reply: { seat: 1, card: 'Rug', target: 2 },
      skipping: [3],
      cardValues: VALUES,
      lastMove: { seat: 1, ...roll, play: { card: 'Rug', target: 2, pushed: [{ seat: 2, from: 9, landed: 5, to: 5 }], reply: 'TimedOut' } },
    };
    expect(toRaceBoard(waiting)).toBe(waiting);
    expect(toRaceBoard({ ...waiting, phase: 'Defend' })).toBeNull();
    expect(toRaceBoard({ ...waiting, reply: { seat: 1, card: 'Joker', target: 2 } })).toBeNull();
    expect(toRaceBoard({ ...waiting, skipping: ['3'] })).toBeNull();
    expect(toRaceBoard({ ...waiting, lastMove: { seat: 1, ...roll, play: { card: 'Rug', reply: 'Maybe' } } })).toBeNull();
  });

  it('offers Rug on each opponent off the Start and Liquidated on each who isn’t already skipping', () => {
    render({ phase: 'Play', hand: ['Rug', 'Liquidated'], playable: ['Rug', 'Liquidated'], skipping: [2] });

    expect(labels()).toEqual(['Rug: Carol 4 back', 'Liquidated: Carol skips a turn']);
    (page.querySelectorAll<HTMLButtonElement>('[data-play]')[0]).click();
    expect(plays).toEqual([{ card: 'Rug', target: 3 }]);
  });

  it('asks the target to block with HODL or take it, and says who everyone else waits for', () => {
    const attack = { phase: 'Reply' as const, reply: { seat: 2, card: 'Rug' as const, target: 1 }, lastMove: { seat: 2, ...roll, play: { card: 'Rug' as const, target: 1 } } };
    render({ ...attack, hand: ['Hodl', 'Pump'], playable: ['Pump'] }, bobsTurn);

    expect(text('[data-turn-line]')).toBe('Bob played Rug on you. Block it with HODL or take it.');
    expect(text('[data-last-play]')).toBe('Bob played Rug on you, waiting for an answer.');
    expect(page.querySelector('[data-roll]')).toBeNull();
    expect(page.querySelector('[data-end-turn]')).toBeNull();
    page.querySelector<HTMLButtonElement>('[data-block]')?.click();
    page.querySelector<HTMLButtonElement>('[data-pass]')?.click();
    expect(plays).toEqual([{ card: 'Hodl' }]);
    expect(passes).toBe(1);

    // Without HODL there is only Take it, and it waits while a command is on its way.
    render({ ...attack, hand: ['Pump'] }, bobsTurn, true);
    expect(text('[data-turn-line]')).toBe('Bob played Rug on you. You have no HODL to block it.');
    expect(page.querySelector('[data-block]')).toBeNull();
    expect(page.querySelector<HTMLButtonElement>('[data-pass]')?.disabled).toBe(true);

    // The attacker and everyone else just wait.
    render({ phase: 'Reply', reply: { seat: 1, card: 'Liquidated', target: 3 }, hand: ['Hodl'] });
    expect(text('[data-turn-line]')).toBe('Waiting for Carol to answer your Liquidated.');
    expect(page.querySelector('[data-reply]')).toBeNull();
    expect(page.querySelector('[data-plays]')).toBeNull();
    render({ phase: 'Reply', reply: { seat: 2, card: 'Whale', target: 3 } }, bobsTurn);
    expect(text('[data-turn-line]')).toBe('Waiting for Carol to answer Bob’s Whale.');
  });

  it('says how an attack ended and who skips a turn', () => {
    const played = (play: NonNullable<RaceBoard['lastMove']>['play'], members = bobsTurn, skipping?: number[]) => {
      render({ phase: 'Move', lastMove: { seat: 1, ...roll, play }, skipping }, members);
      return text('[data-last-play]');
    };

    expect(played({ card: 'Rug', target: 2, reply: 'Blocked' })).toBe('You played Rug on Bob, blocked with HODL.');
    expect(played({ card: 'Rug', target: 3, pushed: [{ seat: 3, from: 12, landed: 8, to: 8 }], reply: 'Allowed' })).toBe(
      'You played Rug: Carol back to tile 8.',
    );
    expect(played({ card: 'Rug', target: 3, pushed: [{ seat: 3, from: 13, landed: 9, to: 7, effect: 'Setback' }] })).toBe(
      'You played Rug: Carol back to tile 9, then set back to tile 7.',
    );
    expect(played({ card: 'Liquidated', target: 3, reply: 'TimedOut' }, bobsTurn, [3])).toBe(
      'You played Liquidated: Carol skips their next turn. Carol didn’t answer in time.',
    );
    expect(text('[data-skipping]')).toBe('Skips a turn');
    expect(page.querySelector('[data-seat="3"] [data-skipping]')).not.toBeNull();
    render({ phase: 'Move', lastMove: { seat: 2, ...roll, play: { card: 'Liquidated', target: 1 } } }, bobsTurn);
    expect(text('[data-last-play]')).toBe('Bob played Liquidated: you skip your next turn.');
  });
});

describe('Chog Race board abilities (STORY-055)', () => {
  let fixture: ComponentFixture<RaceBoardView>;
  let page: HTMLElement;
  let uses: unknown[];

  const pawns = [
    { seat: 1, tile: 5, cards: 1 },
    { seat: 2, tile: 0, cards: 0 },
    { seat: 3, tile: 12, cards: 2 },
  ];
  const ability = (id: string, name: string, usesLeft = 1) => ({
    id,
    name,
    description: '',
    target: (id === 'card_steal' ? 'Opponent' : 'None') as RoomAbility['target'],
    maxUses: 1,
    usesLeft,
  });
  const players = (yours: RoomAbility, yourTurn = true) => [
    member('Alice', 1, { isYou: true, isTurn: yourTurn, ability: yours, hasAbility: true }),
    member('Bob', 2, { isTurn: !yourTurn, ability: ability('attack_block', 'Shield'), hasAbility: true }),
    member('Carol', 3, { ability: ability('card_steal', 'Card Steal'), hasAbility: true }),
  ];

  function render(board: Partial<RaceBoard>, members: RoomMember[], rolling = false): void {
    fixture.componentRef.setInput('board', { ...RACE_BOARD, pawns, deck: 5, maxHand: 3, hand: ['Pump'], ...board });
    fixture.componentRef.setInput('members', members);
    fixture.componentRef.setInput('rolling', rolling);
    fixture.detectChanges();
  }

  const text = (selector: string) => page.querySelector(selector)?.textContent?.trim();
  const labels = () => Array.from(page.querySelectorAll('[data-ability]')).map((button) => button.textContent?.trim());

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [RaceBoardView] });
    fixture = TestBed.createComponent(RaceBoardView);
    page = fixture.nativeElement as HTMLElement;
    uses = [];
    fixture.componentInstance.useAbility.subscribe((use) => uses.push(use));
  });

  it('reads armed abilities, the card Card Vision shows and the latest ability used', () => {
    const board = {
      ...RACE_BOARD,
      pawns: [{ seat: 1, tile: 0, cards: 1, boost: 2, shielded: true }],
      lastMove: { seat: 1, roll: 2, from: 0, landed: 4, to: 6, effect: 'Boost', boost: 2, play: { card: 'Rug', target: 2, reply: 'Shielded' } },
      next: 'Whale',
      ability: { seat: 1, ability: 'card_steal', target: 2, card: 'Hodl' },
    };
    expect(toRaceBoard(board)).toBe(board);
    expect(toRaceBoard({ ...board, next: 'Joker' })).toBeNull();
    expect(toRaceBoard({ ...board, ability: { seat: 1 } })).toBeNull();
    expect(toRaceBoard({ ...board, ability: { seat: 1, ability: 'card_steal', card: 'Joker' } })).toBeNull();
    expect(toRaceBoard({ ...board, pawns: [{ seat: 1, tile: 0, shielded: 'yes' }] })).toBeNull();
    expect(toRaceBoard({ ...board, pawns: [{ seat: 1, tile: 0, boost: -2 }] })).toBeNull();
    const pushedDraw = { card: 'Reverse', pushed: [{ seat: 2, from: 8, landed: 7, to: 7, draw: 'Drawn', card: 'Rug', reshuffled: true }] };
    const withPush = { ...board, lastMove: { seat: 1, roll: 1, from: 0, landed: 1, to: 1, play: pushedDraw } };
    expect(toRaceBoard(withPush)).toBe(withPush);
    expect(toRaceBoard({ ...withPush, lastMove: { ...withPush.lastMove, play: { card: 'Reverse', pushed: [{ seat: 2, from: 8, landed: 7, to: 7, draw: 'Lost' }] } } })).toBeNull();
    expect(toRaceBoard({ ...withPush, lastMove: { ...withPush.lastMove, play: { card: 'Reverse', pushed: [{ seat: 2, from: 8, landed: 7, to: 7, card: 'Joker' }] } } })).toBeNull();
  });

  it('offers Speed Boost only before the roll and while not armed', () => {
    const speed = ability('movement_boost', 'Speed Boost');
    render({ phase: 'Move' }, players(speed));
    expect(labels()).toEqual(['Use Speed Boost: +2 to your roll']);
    page.querySelector<HTMLButtonElement>('[data-ability]')?.click();
    expect(uses).toEqual([{ abilityId: 'movement_boost', target: undefined }]);

    render({ phase: 'Play', playable: ['Pump'] }, players(speed));
    expect(labels()).toEqual([]);
    render({ phase: 'Move', pawns: [{ seat: 1, tile: 5, cards: 1, boost: 2 }, ...pawns.slice(1)] }, players(speed));
    expect(labels()).toEqual([]);
    expect(text('[data-seat="1"] [data-boosted]')).toBe('Boost +2');
  });

  it('offers Card Steal on each opponent holding a card while your hand has room', () => {
    render({ phase: 'Move' }, players(ability('card_steal', 'Card Steal')));
    expect(labels()).toEqual(['Card Steal: from Carol']);
    page.querySelector<HTMLButtonElement>('[data-ability]')?.click();
    expect(uses).toEqual([{ abilityId: 'card_steal', target: 3 }]);

    render({ phase: 'Play', hand: ['Pump', 'Rug', 'Hodl'], playable: ['Pump', 'Rug'] }, players(ability('card_steal', 'Card Steal')));
    expect(labels()).toEqual([]);
  });

  it('offers Shield and Card Vision before or after the roll, once, and nothing off your turn', () => {
    render({ phase: 'Play', playable: ['Pump'] }, players(ability('attack_block', 'Shield')));
    expect(labels()).toEqual(['Use Shield']);
    render({ phase: 'Move' }, players(ability('card_preview', 'Card Vision')));
    expect(labels()).toEqual(['Use Card Vision: see the next card']);

    render({ phase: 'Move', deck: 0 }, players(ability('card_preview', 'Card Vision')));
    expect(labels()).toEqual([]);
    render({ phase: 'Move' }, players(ability('card_preview', 'Card Vision', 0)));
    expect(labels()).toEqual([]);
    render({ phase: 'Move' }, players(ability('card_preview', 'Card Vision'), false));
    expect(labels()).toEqual([]);
    render({ phase: 'Reply', reply: { seat: 1, card: 'Rug', target: 2 } }, players(ability('card_preview', 'Card Vision')));
    expect(labels()).toEqual([]);
    render({ phase: 'Move' }, players(ability('card_preview', 'Card Vision')), true);
    expect(page.querySelector<HTMLButtonElement>('[data-ability]')?.disabled).toBe(true);
  });

  it('shows raised Shields, the card Card Vision shows you and the latest ability used', () => {
    render(
      {
        phase: 'Move',
        pawns: [pawns[0], { seat: 2, tile: 0, cards: 0, shielded: true }, pawns[2]],
        next: 'Whale',
        ability: { seat: 3, ability: 'card_steal', target: 1, card: 'Hodl' },
      },
      players(ability('card_preview', 'Card Vision', 0)),
    );

    expect(page.querySelector('[data-seat="2"] [data-shielded]')?.textContent?.trim()).toBe('Shielded');
    expect(page.querySelector('[data-seat="1"] [data-shielded]')).toBeNull();
    expect(text('[data-next-card]')).toBe('Next card in the deck: Whale. Only you can see this.');
    expect(text('[data-last-ability]')).toBe('Carol used Card Steal and took HODL from you.');

    render({ phase: 'Move', ability: { seat: 3, ability: 'card_steal', target: 2 } }, players(ability('card_preview', 'Card Vision', 0)));
    expect(text('[data-last-ability]')).toBe('Carol used Card Steal and took a card from Bob.');
    expect(page.querySelector('[data-next-card]')).toBeNull();
    render({ phase: 'Move', ability: { seat: 2, ability: 'attack_block' } }, players(ability('card_preview', 'Card Vision', 0)));
    expect(text('[data-last-ability]')).toBe('Bob used Shield.');
  });

  it('always shows your ability, its uses left and why it can’t be used now', () => {
    const steal = ability('card_steal', 'Card Steal');
    const status = () => text('[data-ability-status]');

    render({ phase: 'Move' }, players(steal, false));
    expect(text('[data-your-ability] h3')).toContain('Your ability: Card Steal');
    expect(text('[data-uses-left]')).toBe('1/1 left');
    expect(status()).toBe('Use it on your turn.');
    render({ phase: 'Move' }, players(steal));
    expect(page.querySelector('[data-your-ability] [data-ability="card_steal"]')).not.toBeNull();
    expect(page.querySelector('[data-ability-status]')).toBeNull();
    render({ phase: 'Move', pawns: [pawns[0], pawns[1], { seat: 3, tile: 12, cards: 0 }] }, players(steal));
    expect(status()).toBe('No opponent holds a card.');
    render({ phase: 'Move', hand: ['Pump', 'Rug', 'Hodl'] }, players(steal));
    expect(status()).toBe('Your hand is full.');
    render({ phase: 'Reply', reply: { seat: 2, card: 'Rug', target: 3 } }, players(steal, false));
    expect(status()).toBe('Not while an attack waits for an answer.');

    render({ phase: 'Play', playable: ['Pump'] }, players(ability('movement_boost', 'Speed Boost')));
    expect(status()).toBe('Only before you roll.');
    render({ phase: 'Move', pawns: [{ seat: 1, tile: 5, cards: 1, boost: 2 }, ...pawns.slice(1)] }, players(ability('movement_boost', 'Speed Boost', 0), false));
    expect(status()).toBe('Armed: your next roll moves 2 more.');
    render({ phase: 'Move', pawns: [{ seat: 1, tile: 5, cards: 1, shielded: true }, ...pawns.slice(1)] }, players(ability('attack_block', 'Shield', 0)));
    expect(status()).toBe('Raised: it blocks the next Rug, Liquidated or Whale played on you.');
    render({ phase: 'Move' }, players(ability('card_preview', 'Card Vision', 0)));
    expect(status()).toBe('Used.');
    expect(text('[data-uses-left]')).toBe('0/1 left');
    render({ phase: 'Move', deck: 0 }, players(ability('card_preview', 'Card Vision')));
    expect(status()).toBe('The deck is empty.');

    // Not once the match is over.
    render({ phase: undefined }, players(steal));
    expect(page.querySelector('[data-your-ability]')).toBeNull();
  });

  it('says what a pawn pushed onto a Card tile drew, naming the card only to its player', () => {
    render(
      {
        phase: 'Move',
        lastMove: {
          seat: 3,
          roll: 1,
          from: 11,
          landed: 12,
          to: 15,
          effect: 'Boost',
          play: {
            card: 'Reverse',
            pushed: [
              { seat: 1, from: 8, landed: 7, to: 7, draw: 'Drawn', card: 'Rug' },
              { seat: 2, from: 10, landed: 9, to: 7, effect: 'Setback', draw: 'Drawn' },
            ],
          },
        },
      },
      players(ability('card_preview', 'Card Vision'), false),
    );

    expect(text('[data-last-play]')).toBe(
      'Carol played Reverse: you back to tile 7 and drew Rug, Bob back to tile 9, then set back to tile 7 and drew a card.',
    );
  });

  it('says when a Speed Boost added to the roll and when a Shield blocked an attack', () => {
    render(
      { phase: 'Move', lastMove: { seat: 1, roll: 3, from: 0, landed: 5, to: 5, boost: 2, play: { card: 'Rug', target: 2, reply: 'Shielded' } } },
      players(ability('movement_boost', 'Speed Boost', 0), false),
    );

    expect(text('[data-last-move]')).toBe('You rolled 3 (+2 Speed Boost) and moved to tile 5.');
    expect(text('[data-last-play]')).toBe('You played Rug on Bob, blocked by a Shield.');
  });
});

/** How the DOM reports a `#rrggbb` colour set as a style. */
function hexToRgb(hex: string): string {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
  return `rgb(${r}, ${g}, ${b})`;
}
