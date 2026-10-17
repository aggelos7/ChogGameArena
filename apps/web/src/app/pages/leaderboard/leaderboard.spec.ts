import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Leaderboard, LeaderboardEntry } from '../../leaderboards/leaderboard-api';
import { CATALOGUE } from '../../testing';
import { LeaderboardPage } from './leaderboard';

describe('Leaderboard page', () => {
  const avatar = { id: 'classic', name: 'Classic', color: '#5b2bd6' };
  const entry = (place: number, displayName: string, xp: number, wins: number, isYou = false): LeaderboardEntry => ({
    place,
    displayName,
    avatar,
    xp,
    wins,
    isYou,
  });
  const board: Leaderboard = {
    game: 'all',
    period: 'all-time',
    season: null,
    entries: [entry(1, 'Alice', 40, 2), entry(2, 'Speedy Chog', 35, 1, true), entry(3, 'Bob', 10, 0)],
    you: entry(2, 'Speedy Chog', 35, 1, true),
  };

  let fixture: ComponentFixture<LeaderboardPage>;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [LeaderboardPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LeaderboardPage);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const places = () => [...element().querySelectorAll<HTMLElement>('[data-place]')];
  const button = (selector: string) => element().querySelector(selector) as HTMLButtonElement;

  function expectBoard(game: string, period: string): TestRequest {
    return http.expectOne((req) => req.url === '/api/leaderboards' && req.params.get('game') === game && req.params.get('period') === period);
  }

  function load(body: Leaderboard = board, games = CATALOGUE) {
    http.expectOne('/api/games').flush(games);
    expectBoard('all', 'all-time').flush(body);
    fixture.detectChanges();
  }

  it('lists the places with name, XP and wins, marking the viewer', () => {
    load();

    expect(places().map((li) => li.dataset['place'])).toEqual(['1', '2', '3']);
    const [first, second] = places();
    expect(first.querySelector('.name')?.textContent).toBe('Alice');
    expect(first.querySelector('[data-xp]')?.textContent).toBe('40 XP');
    expect(first.querySelector('[data-wins]')?.textContent).toBe('2 wins');
    expect(first.classList).not.toContain('you');
    expect(second.classList).toContain('you');
    expect(second.querySelector('.tag')?.textContent).toBe('You');
    expect(second.querySelector('[data-wins]')?.textContent).toBe('1 win');
    expect(element().querySelector('[data-own-place]')).toBeNull();
    expect(element().querySelector('[data-season]')).toBeNull();
  });

  it('offers a board per catalog game besides all games, and all-time or this season', () => {
    load();

    const games = [...element().querySelectorAll<HTMLButtonElement>('[data-game]')];
    expect(games.map((b) => b.textContent?.trim())).toEqual(['All games', 'Chog Life', 'Chog Race', 'Chog Fix']);
    expect(games.map((b) => b.getAttribute('aria-pressed'))).toEqual(['true', 'false', 'false', 'false']);
    const periods = [...element().querySelectorAll<HTMLButtonElement>('[data-period]')];
    expect(periods.map((b) => b.textContent?.trim())).toEqual(['All-time', 'This season']);
  });

  it('loads the chosen game and season board and names the season', () => {
    load();

    button('[data-game="chog-race"]').click();
    fixture.detectChanges();
    expectBoard('chog-race', 'all-time').flush({ ...board, game: 'chog-race' });
    button('[data-period="season"]').click();
    fixture.detectChanges();
    expectBoard('chog-race', 'season').flush({
      ...board,
      game: 'chog-race',
      period: 'season',
      season: { startsAt: '2026-10-01T00:00:00Z', endsAt: '2026-11-01T00:00:00Z' },
    });
    fixture.detectChanges();

    expect(button('[data-game="chog-race"]').getAttribute('aria-pressed')).toBe('true');
    expect(button('[data-period="season"]').getAttribute('aria-pressed')).toBe('true');
    expect(element().querySelector('[data-season]')?.textContent).toBe('Season: October 2026 (UTC)');

    button('[data-period="season"]').click();
    http.expectNone('/api/leaderboards');
  });

  it('shows only the latest board when the choice changes before a board arrives', () => {
    load();
    button('[data-game="chog-life"]').click();
    const stale = expectBoard('chog-life', 'all-time');

    button('[data-game="chog-race"]').click();
    expectBoard('chog-race', 'all-time').flush({ ...board, entries: [entry(1, 'Racer', 15, 1)], you: null });
    fixture.detectChanges();

    expect(stale.cancelled).toBe(true);
    expect(places().map((li) => li.querySelector('.name')?.textContent)).toEqual(['Racer']);
  });

  it('adds the viewer’s own place below the top places', () => {
    const top = Array.from({ length: 50 }, (_, i) => entry(i + 1, `Player ${i + 1}`, 100 - i, 1));
    load({ ...board, entries: top, you: entry(52, 'Speedy Chog', 10, 0, true) });

    expect(places()).toHaveLength(51);
    const own = element().querySelector('[data-own-place]') as HTMLElement;
    expect(own.dataset['place']).toBe('52');
    expect(own.querySelector('.name')?.textContent).toBe('Speedy Chog');
    expect(own.classList).toContain('you');
    expect(places()[places().length - 1]).toBe(own);
  });

  it('says so when the board is empty and the viewer isn’t on it', () => {
    load({ ...board, entries: [], you: null });

    expect(element().querySelector('[data-empty]')?.textContent).toContain('No one has XP on this board yet');
    expect(element().querySelector('[data-not-ranked]')?.textContent).toContain('complete a match to earn XP');
    expect(element().querySelector('ol')).toBeNull();
  });

  it('still shows the all games board when the catalog can’t be loaded', () => {
    http.expectOne('/api/games').flush(null, { status: 503, statusText: 'Unavailable' });
    expectBoard('all', 'all-time').flush(board);
    fixture.detectChanges();

    expect([...element().querySelectorAll('[data-game]')].map((b) => b.textContent?.trim())).toEqual(['All games']);
    expect(places()).toHaveLength(3);
  });

  it('shows an error with retry when the board cannot be loaded', () => {
    http.expectOne('/api/games').flush(CATALOGUE);
    expectBoard('all', 'all-time').flush(null, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('could not be loaded');
    (element().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    expectBoard('all', 'all-time').flush(board);
    fixture.detectChanges();

    expect(places()).toHaveLength(3);
  });
});
