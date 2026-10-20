import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Route, Router, provideRouter } from '@angular/router';
import { App } from './app';
import { appRoutes } from './app.routes';
import { CurrentPlayer, PlayerSession, PlayerSessionStatus } from './identity/player-session';
import { Avatar } from './profile/profile-api';
import { PAGE_LOAD, withNavigationErrorState } from './shell/navigation-status';

@Component({ template: '<h1>Stub page</h1>' })
class StubPage {}

/** Lightweight pages so shell behaviour is tested without the real pages' HTTP calls. */
const stubRoutes: Route[] = [
  { path: '', pathMatch: 'full', component: StubPage },
  { path: 'chogs', component: StubPage },
  { path: 'profile', component: StubPage },
  { path: 'broken', loadComponent: () => Promise.reject(new Error('chunk failed')) },
  { path: 'slow', loadComponent: () => slowPage.promise },
];

let slowPage: { promise: Promise<typeof StubPage>; resolve: (page: typeof StubPage) => void };

function deferredPage(): typeof slowPage {
  let resolve!: (page: typeof StubPage) => void;
  const promise = new Promise<typeof StubPage>((r) => (resolve = r));
  return { promise, resolve };
}

describe('App shell', () => {
  const session = {
    player: signal<CurrentPlayer | null>(null),
    status: signal<PlayerSessionStatus>('loading'),
    avatar: signal<Avatar | null>(null),
    updateAvatar(avatar: Avatar | null) {
      this.avatar.set(avatar);
    },
    ensureGuest: vi.fn(),
  };
  const pageLoad = vi.fn();

  async function setup(routes: Route[]) {
    session.player.set(null);
    session.status.set('loading');
    session.ensureGuest.mockReset();
    pageLoad.mockReset();
    slowPage = deferredPage();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter(routes, ...withNavigationErrorState()),
        // The open-room banner asks the server once there is a session; these specs leave it unanswered.
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: PlayerSession, useValue: session },
        { provide: PAGE_LOAD, useValue: pageLoad },
      ],
    }).compileComponents();
  }

  async function render() {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  async function navigate(fixture: Awaited<ReturnType<typeof render>>['fixture'], url: string) {
    const result = await TestBed.inject(Router).navigateByUrl(url);
    await fixture.whenStable();
    return result;
  }

  describe('with the app routes', () => {
    beforeEach(() => setup(appRoutes));

    it('renders the brand and main navigation', async () => {
      const { element } = await render();

      expect(element.querySelector('.brand')?.textContent).toContain('Chog Kingdom');
      const links = Array.from(element.querySelectorAll('nav[aria-label="Main"] a'));
      expect(links.map((a) => a.getAttribute('href'))).toEqual(['/', '/chogs', '/ladder']);
      expect(element.querySelector('main router-outlet')).not.toBeNull();
    });

    it('links the terms of use and privacy notice from the footer of every page', async () => {
      const { element } = await render();

      const links = Array.from(element.querySelectorAll('footer nav[aria-label="Legal"] a'));
      expect(links.map((a) => [a.getAttribute('href'), a.textContent?.trim()])).toEqual([
        ['/terms', 'Terms of use'],
        ['/privacy', 'Privacy'],
      ]);
    });

    it('shows the player’s avatar and name, leading to their profile, once the session is ready', async () => {
      session.player.set({ playerId: 'x', displayName: 'Chog 4821', kind: 'Guest', walletAddress: null });
      session.status.set('ready');

      const { fixture, element } = await render();
      const link = element.querySelector('a.player') as HTMLAnchorElement;
      expect(link.getAttribute('href')).toBe('/profile');
      expect(link.getAttribute('aria-label')).toBe('Your profile: Chog 4821');
      expect(link.textContent?.trim()).toBe('Chog 4821');
      // The name shows alone until the profile with the avatar arrives.
      expect(link.querySelector('app-avatar-badge')).toBeNull();

      const http = TestBed.inject(HttpTestingController);
      http.expectOne('/api/profile').flush({ avatar: { id: 'classic', name: 'Classic', color: '#5b2bd6', imageUrl: '/api/avatars/a1' } });
      await fixture.whenStable();
      expect(link.querySelector('app-avatar-badge img')?.getAttribute('src')).toBe('/api/avatars/a1');
    });

    it('keeps the name alone when the profile cannot be loaded', async () => {
      session.player.set({ playerId: 'x', displayName: 'Chog 4821', kind: 'Guest', walletAddress: null });
      session.status.set('ready');

      const { fixture, element } = await render();
      TestBed.inject(HttpTestingController).expectOne('/api/profile').flush('down', { status: 503, statusText: 'Service Unavailable' });
      await fixture.whenStable();

      expect(element.querySelector('a.player')?.textContent?.trim()).toBe('Chog 4821');
      expect(element.querySelector('a.player app-avatar-badge')).toBeNull();
    });

    it('offers a retry when the guest session could not be established', async () => {
      session.status.set('error');

      const { element } = await render();
      (element.querySelector('.player-error button') as HTMLButtonElement).click();

      expect(element.querySelector('.player-error')?.textContent).toContain('Guest sign-in unavailable');
      expect(session.ensureGuest).toHaveBeenCalledOnce();
    });
  });

  describe('keyboard and screen reader navigation', () => {
    beforeEach(() => setup(stubRoutes));

    it('starts with a skip link that moves focus to the main content', async () => {
      const { element } = await render();
      const skip = element.querySelector('a') as HTMLAnchorElement;
      const main = element.querySelector('main') as HTMLElement;

      expect(skip.textContent?.trim()).toBe('Skip to main content');
      expect(main.id).toBe('main-content');
      expect(main.getAttribute('tabindex')).toBe('-1');

      const click = new MouseEvent('click', { cancelable: true });
      skip.dispatchEvent(click);

      expect(click.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(main);
    });

    it('only uses natively focusable controls in the shell', async () => {
      session.status.set('error');
      const { element } = await render();

      for (const control of Array.from(element.querySelectorAll('.skip-link, header a, header button'))) {
        expect(control.matches('a[href], button:not([disabled])')).toBe(true);
        expect(control.getAttribute('tabindex')).toBeNull();
      }
    });

    it('marks the current page link with aria-current', async () => {
      const { fixture, element } = await render();
      await navigate(fixture, '/chogs');

      const current = Array.from(element.querySelectorAll('nav a[aria-current="page"]'));
      expect(current.map((a) => a.textContent?.trim())).toEqual(['My Chogs']);
    });

    it('moves focus to the main content after an in-app navigation, but not on first load', async () => {
      const { fixture, element } = await render();
      const main = element.querySelector('main') as HTMLElement;

      await navigate(fixture, '/');
      expect(document.activeElement).not.toBe(main);

      const chogsLink = element.querySelector('nav a[href="/chogs"]') as HTMLAnchorElement;
      chogsLink.focus();
      await navigate(fixture, '/chogs');

      expect(document.activeElement).toBe(main);
    });

    it('shows a reloadable error when a page cannot be loaded, and keeps the current page', async () => {
      const { fixture, element } = await render();
      await navigate(fixture, '/profile');

      expect(await navigate(fixture, '/broken')).toBe(false);

      const alert = element.querySelector('main [role="alert"]') as HTMLElement;
      expect(alert.textContent).toContain('This page could not be loaded');
      expect(TestBed.inject(Router).url).toBe('/profile');
      expect(element.querySelector('main h1')?.textContent).toBe('Stub page');

      (alert.querySelector('button') as HTMLButtonElement).click();
      expect(pageLoad).toHaveBeenCalledWith('/broken');
    });

    it('shows a page-loading indicator and marks main busy while a page loads', async () => {
      const { fixture, element } = await render();
      const pending = TestBed.inject(Router).navigateByUrl('/slow');
      // whenStable() would wait for the pending navigation itself, so render manually.
      await new Promise((resolve) => setTimeout(resolve));
      fixture.detectChanges();

      expect(element.querySelector('[role="progressbar"]')?.getAttribute('aria-label')).toBe('Loading page');
      expect(element.querySelector('main')?.getAttribute('aria-busy')).toBe('true');

      slowPage.resolve(StubPage);
      await pending;
      await fixture.whenStable();

      expect(element.querySelector('[role="progressbar"]')).toBeNull();
      expect(element.querySelector('main')?.getAttribute('aria-busy')).toBe('false');
    });

    it('clears the page error after the next successful navigation', async () => {
      const { fixture, element } = await render();
      await navigate(fixture, '/broken');
      expect(element.querySelector('main [role="alert"]')).not.toBeNull();

      await navigate(fixture, '/profile');

      expect(element.querySelector('main [role="alert"]')).toBeNull();
    });
  });
});
