import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { appRoutes } from './app.routes';
import { CurrentPlayer, PlayerSession, PlayerSessionStatus } from './identity/player-session';

describe('App shell', () => {
  const session = {
    player: signal<CurrentPlayer | null>(null),
    status: signal<PlayerSessionStatus>('loading'),
    ensureGuest: vi.fn(),
  };

  beforeEach(async () => {
    session.player.set(null);
    session.status.set('loading');
    session.ensureGuest.mockReset();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(appRoutes), { provide: PlayerSession, useValue: session }],
    }).compileComponents();
  });

  async function render() {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders the brand and main navigation', async () => {
    const element = await render();

    expect(element.querySelector('.brand')?.textContent).toContain('Chog Game Arena');
    const links = Array.from(element.querySelectorAll('nav[aria-label="Main"] a'));
    expect(links.map((a) => a.getAttribute('href'))).toEqual(['/', '/games']);
    expect(element.querySelector('main router-outlet')).not.toBeNull();
  });

  it('shows the guest display name once the session is ready', async () => {
    session.player.set({ playerId: 'x', displayName: 'Chog 4821', kind: 'Guest' });
    session.status.set('ready');

    const element = await render();

    expect(element.querySelector('.player')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('Playing as Chog 4821');
  });

  it('offers a retry when the guest session could not be established', async () => {
    session.status.set('error');

    const element = await render();
    (element.querySelector('.player-error button') as HTMLButtonElement).click();

    expect(element.querySelector('.player-error')?.textContent).toContain('Guest sign-in unavailable');
    expect(session.ensureGuest).toHaveBeenCalledOnce();
  });
});
