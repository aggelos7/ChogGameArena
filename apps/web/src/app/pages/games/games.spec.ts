import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { GameSummary } from '../../games/game-catalog';
import { CATALOGUE } from '../../testing';
import { Games } from './games';

describe('Games page', () => {
  let fixture: ComponentFixture<Games>;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [Games],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Games);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const card = (id: string) => element().querySelector(`[data-game-id="${id}"]`) as HTMLElement;

  function respond(body: GameSummary[] | null, status = 200) {
    const req = http.expectOne('/api/games');
    expect(req.request.method).toBe('GET');
    if (status === 200) req.flush(body);
    else req.flush('error', { status, statusText: 'Error' });
    fixture.detectChanges();
  }

  it('shows a loading state until the catalogue arrives', () => {
    expect(element().querySelector('[role="status"]')?.textContent).toContain('Loading games');
    respond(CATALOGUE);
    expect(element().querySelector('[role="status"]')).toBeNull();
  });

  it('renders a card with name, description and status label for every game', () => {
    respond(CATALOGUE);

    expect(element().querySelectorAll('.game-card')).toHaveLength(3);
    expect(card('chog-life').querySelector('h2')?.textContent).toBe('Chog Life');
    expect(card('chog-life').querySelector('.description')?.textContent).toBe('Life path game.');
    expect(card('chog-life').querySelector('.status')?.textContent?.trim()).toBe('Available');
    expect(card('chog-race').querySelector('.status')?.textContent?.trim()).toBe('Coming soon');
    expect(card('chog-fix').querySelector('.status')?.textContent?.trim()).toBe('Under maintenance');
  });

  it('lets only available games be launched', () => {
    respond(CATALOGUE);

    const play = card('chog-life').querySelector('a.play') as HTMLAnchorElement;
    expect(play.getAttribute('href')).toBe('/games/chog-life');
    expect(card('chog-life').querySelector('button.play')).toBeNull();

    for (const id of ['chog-race', 'chog-fix']) {
      const button = card(id).querySelector('button.play') as HTMLButtonElement;
      expect(button.disabled).toBe(true);
      expect(card(id).querySelector('a')).toBeNull();
    }
  });

  it('associates each Play control with the game name and status', () => {
    respond(CATALOGUE);

    expect(card('chog-race').querySelector('.play')?.getAttribute('aria-describedby')).toBe(
      'game-name-chog-race game-status-chog-race',
    );
  });

  it('shows an error with retry when the catalogue cannot be loaded', () => {
    respond(null, 500);

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('Games could not be loaded');
    expect(element().querySelector('.game-card')).toBeNull();

    (element().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    respond(CATALOGUE);

    expect(element().querySelectorAll('.game-card')).toHaveLength(3);
  });

  it('shows an empty message when no games are listed', () => {
    respond([]);

    expect(element().textContent).toContain('No games are listed yet.');
  });
});
