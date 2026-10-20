import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { appRoutes } from './app.routes';
import { CATALOGUE } from './testing';

describe('app routes', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter(appRoutes, withComponentInputBinding()), provideHttpClient(), provideHttpClientTesting()],
    });
    harness = await RouterTestingHarness.create();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const heading = () => (harness.routeNativeElement as HTMLElement).querySelector('h1')?.textContent?.trim();
  const url = () => TestBed.inject(Router).url;

  /** Navigates, answering the catalogue request the route makes with `respond`. */
  async function navigate(path: string, respond: (req: ReturnType<HttpTestingController['expectOne']>) => void) {
    const navigation = harness.navigateByUrl(path);
    await new Promise((resolve) => setTimeout(resolve));
    respond(http.expectOne('/api/games'));
    await navigation;
    harness.detectChanges();
  }

  it('renders the home page with the game catalogue at /', async () => {
    await harness.navigateByUrl('/');
    http.expectOne('/api/games').flush(CATALOGUE);
    harness.detectChanges();

    expect(heading()).toBe('Chog Kingdom');
    expect((harness.routeNativeElement as HTMLElement).querySelectorAll('.game-card')).toHaveLength(3);
    expect(TestBed.inject(Title).getTitle()).toBe('Chog Kingdom');
  });

  it('redirects the old /games page to the home page', async () => {
    await harness.navigateByUrl('/games');
    http.expectOne('/api/games').flush(CATALOGUE);

    expect(url()).toBe('/');
    expect(heading()).toBe('Chog Kingdom');
  });

  it('renders the My Chogs page at /chogs', async () => {
    await harness.navigateByUrl('/chogs');
    http.expectOne('/api/characters').flush({ nftStatus: 'NoWallet', nfts: [], standard: [], selected: { kind: 'Standard', id: 'classic' } });

    expect(heading()).toBe('My Chogs');
    expect(TestBed.inject(Title).getTitle()).toBe('My Chogs · Chog Kingdom');
  });

  it.each([
    ['/terms', 'Terms of use'],
    ['/privacy', 'Privacy'],
    ['/moderation', 'Reported photos'],
  ])('renders %s', async (path, title) => {
    await harness.navigateByUrl(path);
    http.match(() => true); // the pages' own requests are covered by their specs

    expect(url()).toBe(path);
    expect(heading()).toBe(title);
    expect(TestBed.inject(Title).getTitle()).toBe(`${title} · Chog Kingdom`);
  });

  it.each(['/ladder', '/leaderboard'])('renders the ladder at %s', async (path) => {
    await harness.navigateByUrl(path);
    http.match(() => true); // the ladder's own requests are covered by its spec

    expect(url()).toBe('/ladder');
    expect(heading()).toBe('Ladder');
    expect(TestBed.inject(Title).getTitle()).toBe('Ladder · Chog Kingdom');
  });

  it('opens a room at /rooms/:code', async () => {
    await harness.navigateByUrl('/rooms/K7QF3M');
    http
      .expectOne('/api/rooms/K7QF3M')
      .flush({ code: 'K7QF3M', gameType: 'chog-life', status: 'Waiting', minPlayers: 2, capacity: 6, members: [] });
    http.expectOne('/api/games').flush(CATALOGUE);
    harness.detectChanges();

    expect(heading()).toBe('Chog Life room');
    expect(TestBed.inject(Title).getTitle()).toBe('Room · Chog Kingdom');
  });

  it('joins from an invite link at /join/:code and replaces it with the room', async () => {
    await harness.navigateByUrl('/join/k7qf3m');
    http
      .expectOne('/api/rooms/K7QF3M/join')
      .flush({ code: 'K7QF3M', gameType: 'chog-life', status: 'Waiting', minPlayers: 2, capacity: 6, members: [] });
    // The room page loads lazily after the join; wait for that navigation to finish.
    for (let tick = 0; tick < 50 && url() !== '/rooms/K7QF3M'; tick++) {
      await new Promise((resolve) => setTimeout(resolve));
    }
    harness.detectChanges();
    http
      .expectOne('/api/rooms/K7QF3M')
      .flush({ code: 'K7QF3M', gameType: 'chog-life', status: 'Waiting', minPlayers: 2, capacity: 6, members: [] });
    http.expectOne('/api/games').flush(CATALOGUE);
    harness.detectChanges();

    expect(url()).toBe('/rooms/K7QF3M');
  });

  it('redirects unknown URLs to the home page', async () => {
    await harness.navigateByUrl('/does-not-exist');
    http.expectOne('/api/games').flush(CATALOGUE);

    expect(url()).toBe('/');
    expect(heading()).toBe('Chog Kingdom');
  });

  describe('launching a game at /games/:gameId', () => {
    it('opens the lobby for an available game', async () => {
      await navigate('/games/chog-life', (req) => req.flush(CATALOGUE));

      expect(url()).toBe('/games/chog-life');
      expect(heading()).toBe('Chog Life');
    });

    it.each(['chog-race', 'chog-fix', 'not-a-game'])('redirects %s to the home catalogue', async (gameId) => {
      await navigate(`/games/${gameId}`, (req) => req.flush(CATALOGUE));
      http.expectOne('/api/games').flush(CATALOGUE); // the catalogue page loads after the redirect

      expect(url()).toBe('/');
      expect(heading()).toBe('Chog Kingdom');
    });

    it('redirects to the home catalogue when the catalogue cannot be loaded', async () => {
      await navigate('/games/chog-life', (req) => req.flush('down', { status: 503, statusText: 'Service Unavailable' }));
      http.expectOne('/api/games').flush(CATALOGUE);

      expect(url()).toBe('/');
    });
  });
});
