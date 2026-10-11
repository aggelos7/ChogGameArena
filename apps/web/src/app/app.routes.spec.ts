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

  it('renders the home page at /', async () => {
    await harness.navigateByUrl('/');

    expect(heading()).toBe('Chog Game Arena');
    expect(TestBed.inject(Title).getTitle()).toBe('Chog Game Arena');
  });

  it('renders the games page at /games', async () => {
    await harness.navigateByUrl('/games');
    http.expectOne('/api/games').flush(CATALOGUE);

    expect(heading()).toBe('Games');
    expect(TestBed.inject(Title).getTitle()).toBe('Games · Chog Game Arena');
  });

  it('renders the My Chogs page at /chogs', async () => {
    await harness.navigateByUrl('/chogs');
    http.expectOne('/api/characters').flush({ nftStatus: 'NoWallet', nfts: [], standard: [], selected: { kind: 'Standard', id: 'classic' } });

    expect(heading()).toBe('My Chogs');
    expect(TestBed.inject(Title).getTitle()).toBe('My Chogs · Chog Game Arena');
  });

  it('redirects unknown URLs to the home page', async () => {
    await harness.navigateByUrl('/does-not-exist');

    expect(url()).toBe('/');
    expect(heading()).toBe('Chog Game Arena');
  });

  describe('launching a game at /games/:gameId', () => {
    it('opens the lobby for an available game', async () => {
      await navigate('/games/chog-life', (req) => req.flush(CATALOGUE));

      expect(url()).toBe('/games/chog-life');
      expect(heading()).toBe('Chog Life');
    });

    it.each(['chog-race', 'chog-fix', 'not-a-game'])('redirects %s to the catalogue', async (gameId) => {
      await navigate(`/games/${gameId}`, (req) => req.flush(CATALOGUE));
      http.expectOne('/api/games').flush(CATALOGUE); // the catalogue page loads after the redirect

      expect(url()).toBe('/games');
      expect(heading()).toBe('Games');
    });

    it('redirects to the catalogue when the catalogue cannot be loaded', async () => {
      await navigate('/games/chog-life', (req) => req.flush('down', { status: 503, statusText: 'Service Unavailable' }));
      http.expectOne('/api/games').flush(CATALOGUE);

      expect(url()).toBe('/games');
    });
  });
});
