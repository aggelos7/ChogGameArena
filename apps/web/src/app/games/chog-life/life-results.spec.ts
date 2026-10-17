import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomMember } from '../../rooms/rooms-api';
import { lifeResult } from '../../testing';
import { LifeResultsView, toLifeResult } from './life-results';

function member(displayName: string, seat: number, rank: number | null, result: unknown, overrides: Partial<RoomMember> = {}): RoomMember {
  return {
    displayName,
    avatarId: 'classic',
    collector: null,
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

describe('Chog Life result model', () => {
  it('reads a result the server stored', () => {
    const stored = lifeResult(250, { place: 1, rugsSurvived: 1, degenScore: 70 });

    expect(toLifeResult(stored)).toEqual(stored);
    expect(toLifeResult({ ...stored, place: undefined })?.place).toBeNull();
    expect(toLifeResult(lifeResult(-20))?.portfolio).toBe(-20);
  });

  it('refuses anything else', () => {
    for (const bad of [null, 'nope', [], { ...lifeResult(100), degenScore: -1 }, { ...lifeResult(100), rugsSurvived: '2' }, { ...lifeResult(100), place: 0 }, { portfolio: 100 }]) {
      expect(toLifeResult(bad)).toBeNull();
    }
  });
});

describe('Chog Life results', () => {
  let fixture: ComponentFixture<LifeResultsView>;

  function render(members: RoomMember[]): HTMLElement {
    fixture = TestBed.createComponent(LifeResultsView);
    fixture.componentRef.setInput('members', members);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const texts = (root: Element, selector: string) => Array.from(root.querySelectorAll(selector)).map((e) => e.textContent?.replace(/\s+/g, ' ').trim());

  it('names the winner and lists every player in rank order with their portfolio and statistics', () => {
    const view = render([
      member('Bob', 2, 2, lifeResult(120, { place: 2, airdrops: 2, airdropMon: 35, biggestWin: 35 }), { isYou: true }),
      member('Alice', 1, 1, lifeResult(250, { place: 1, rugsSurvived: 1, liquidations: 2, biggestWin: 100, degenScore: 80 })),
    ]);

    expect(view.querySelector('[data-winner]')?.textContent).toBe('Alice wins with 250 MON!');
    expect(texts(view, '[data-place]')).toEqual(['1st', '2nd']);
    expect(texts(view, '[data-standing] .name')).toEqual(['Alice', 'Bob']);
    expect(texts(view, '[data-portfolio]')).toEqual(['250 MON', '120 MON']);
    expect(texts(view, '[data-summary]')).toEqual([
      'Started with 100 MON (+150 MON) · Reached the finish 1st',
      'Started with 100 MON (+20 MON) · Reached the finish 2nd',
    ]);
    expect(texts(view, '[data-stat="degen-score"]')).toEqual(['80/100', '0/100']);
    expect(texts(view, '[data-stat="biggest-win"]')).toEqual(['+100 MON', '+35 MON']);
    expect(texts(view, '[data-stat="airdrops"]')).toEqual(['0', '2 (+35 MON)']);
    expect(texts(view, '[data-stat="rugs"]')).toEqual(['1', '0']);
    expect(texts(view, '[data-stat="liquidations"]')).toEqual(['2', '0']);
    expect(view.querySelector('.hint')?.textContent).toContain('it doesn’t count towards the ranking');
  });

  it('shows a loss, no biggest win and a player who did not reach the finish', () => {
    const view = render([member('Alice', 1, 1, lifeResult(150)), member('Bob', 2, 2, lifeResult(-30), { isYou: true })]);

    expect(texts(view, '[data-summary]')[1]).toBe('Started with 100 MON (−130 MON) · Didn’t reach the finish');
    expect(view.querySelectorAll('[data-portfolio]')[1].classList).toContain('negative');
    expect(texts(view, '[data-stat="biggest-win"]')).toEqual(['None', 'None']);
  });

  it('shares the win on a tie and says when you won', () => {
    expect(render([member('Alice', 1, 1, lifeResult(200)), member('Bob', 2, 1, lifeResult(200))]).querySelector('[data-winner]')?.textContent).toBe(
      'Alice and Bob share the win with 200 MON!',
    );
    expect(render([member('Alice', 1, 1, lifeResult(200), { isYou: true }), member('Bob', 2, 2, lifeResult(90))]).querySelector('[data-winner]')?.textContent).toBe(
      'You win with 200 MON!',
    );
  });

  it('lists players who left after the others, without results', () => {
    const view = render([member('Carol', 3, 3, null, { hasLeft: true }), member('Alice', 1, 1, lifeResult(200)), member('Bob', 2, 2, lifeResult(90))]);

    expect(texts(view, '[data-standing] .name')).toEqual(['Alice', 'Bob', 'Carol']);
    expect(texts(view, '[data-summary]')[2]).toBe('Left the match');
    expect(view.querySelectorAll('[data-standing]')[2].querySelector('[data-stat]')).toBeNull();
  });
});
