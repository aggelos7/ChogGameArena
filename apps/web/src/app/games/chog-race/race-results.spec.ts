import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomMember } from '../../rooms/rooms-api';
import { RaceResult, RaceResultsView, toRaceResult } from './race-results';

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
    hasAbility: false,
    hasLeft: false,
    isTurn: false,
    rank,
    seat,
    result,
    ...overrides,
  };
}

/** A result on <tile> of the 28-tile track, finished on tile 27. */
function result(tile: number, extra: Partial<RaceResult> = {}): RaceResult {
  return { tile, tilesLeft: 27 - tile, finished: tile === 27, cardsPlayed: 0, attacksLanded: 0, attacksBlocked: 0, degenScore: 0, degenScoreVersion: 1, ...extra };
}

/** A result as stored before STORY-066, without a Degen Score. */
function unscored(tile: number): Partial<RaceResult> {
  const stored: Partial<RaceResult> = result(tile);
  delete stored.degenScore;
  delete stored.degenScoreVersion;
  return stored;
}

describe('Chog Race result model', () => {
  it('reads a result the server stored', () => {
    const stored = result(19, { cardsPlayed: 3, attacksLanded: 1, attacksBlocked: 2, degenScore: 10 });

    expect(toRaceResult(stored)).toEqual(stored);
    expect(toRaceResult(result(27))?.finished).toBe(true);
  });

  it('reads a result stored before the Degen Score (STORY-066)', () => {
    const old = unscored(19);

    expect(toRaceResult(old)).toEqual(old);
  });

  it('refuses anything else', () => {
    for (const bad of [null, 'nope', [], { ...result(4), tile: -1 }, { ...result(4), cardsPlayed: '2' }, { ...result(4), finished: 'no' }, { tile: 4 }, { ...result(4), degenScore: '10' }, { ...result(4), degenScoreVersion: undefined }]) {
      expect(toRaceResult(bad)).toBeNull();
    }
  });
});

describe('Chog Race results', () => {
  let fixture: ComponentFixture<RaceResultsView>;

  function render(members: RoomMember[]): HTMLElement {
    fixture = TestBed.createComponent(RaceResultsView);
    fixture.componentRef.setInput('members', members);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const texts = (root: Element, selector: string) => Array.from(root.querySelectorAll(selector)).map((e) => e.textContent?.replace(/\s+/g, ' ').trim());

  it('draws each player’s profile avatar, not their match Chog (STORY-085)', () => {
    const view = render([
      member('Alice', 1, 1, result(27), { avatar: { id: 'ocean', name: 'Ocean', color: '#1d4ed8', imageUrl: null } }),
      member('Bob', 2, 2, result(19), { avatar: { id: 'classic', name: 'Classic', color: '#5b2bd6', imageUrl: '/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10' } }),
    ]);

    const [alice, bob] = Array.from(view.querySelectorAll('[data-list="standings"] li'));
    const aliceBadge = alice.querySelector('app-avatar-badge .badge') as HTMLElement;
    expect([aliceBadge.style.background, aliceBadge.textContent?.trim()]).toEqual(['rgb(29, 78, 216)', 'A']);
    expect(bob.querySelector('app-avatar-badge img')?.getAttribute('src')).toBe('/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10');
  });

  it('names the winner and lists every player in rank order with where they got and their statistics', () => {
    const view = render([
      member('Bob', 2, 2, result(19, { cardsPlayed: 2, attacksBlocked: 1 }), { isYou: true }),
      member('Carol', 3, 3, result(14)),
      member('Alice', 1, 1, result(27, { cardsPlayed: 4, attacksLanded: 2, degenScore: 20 })),
    ]);

    expect(view.querySelector('[data-winner]')?.textContent).toBe('Alice wins the race!');
    expect(texts(view, '[data-place]')).toEqual(['1st', '2nd', '3rd']);
    expect(texts(view, '[data-standing] .name')).toEqual(['Alice', 'Bob', 'Carol']);
    expect(texts(view, '[data-position]')).toEqual(['Finished', 'Tile 19', 'Tile 14']);
    expect(texts(view, '[data-summary]')).toEqual(['Reached the finish', '8 tiles to go', '13 tiles to go']);
    expect(texts(view, '[data-stat="degen-score"]')).toEqual(['20/100', '0/100', '0/100']);
    expect(texts(view, '[data-stat="cards-played"]')).toEqual(['4', '2', '0']);
    expect(texts(view, '[data-stat="attacks-landed"]')).toEqual(['2', '0', '0']);
    expect(texts(view, '[data-stat="attacks-blocked"]')).toEqual(['0', '1', '0']);
    expect(view.querySelector('.hint')?.textContent).toContain('then by who reached that tile first');
    expect(view.querySelector('.hint')?.textContent).toContain('doesn’t count towards the ranking');
  });

  it('shows a match from before the Degen Score without one (STORY-066)', () => {
    const view = render([member('Alice', 1, 1, unscored(27)), member('Bob', 2, 2, unscored(20))]);

    expect(view.querySelector('[data-stat="degen-score"]')).toBeNull();
    expect(texts(view, '[data-stat="attacks-landed"]')).toEqual(['0', '0']);
    expect(view.querySelector('.hint')?.textContent).not.toContain('Degen Score');
  });

  it('says one tile to go and when you won', () => {
    const view = render([member('Alice', 1, 1, result(27), { isYou: true }), member('Bob', 2, 2, result(26))]);

    expect(view.querySelector('[data-winner]')?.textContent).toBe('You win the race!');
    expect(texts(view, '[data-summary]')[1]).toBe('1 tile to go');
  });

  it('names who got furthest when the time ran out, or who shares the win', () => {
    expect(render([member('Alice', 1, 2, result(12)), member('Bob', 2, 1, result(20))]).querySelector('[data-winner]')?.textContent).toBe(
      'Time’s up! Bob got furthest.',
    );
    expect(render([member('Alice', 1, 1, result(0)), member('Bob', 2, 1, result(0), { isYou: true })]).querySelector('[data-winner]')?.textContent).toBe(
      'Time’s up! Alice and You share the win.',
    );
  });

  it('lists players who left after the others, without results', () => {
    const view = render([member('Carol', 3, 3, null, { hasLeft: true }), member('Alice', 1, 1, result(27)), member('Bob', 2, 2, result(9))]);

    expect(texts(view, '[data-standing] .name')).toEqual(['Alice', 'Bob', 'Carol']);
    expect(texts(view, '[data-summary]')[2]).toBe('Left the match');
    expect(view.querySelectorAll('[data-standing]')[2].querySelector('[data-stat]')).toBeNull();
  });
});
