import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PlayerSession } from '../../identity/player-session';
import { Achievement, Avatar, Profile } from '../../profile/profile-api';
import { WalletPanel } from '../../wallet/wallet-panel';
import { ProfilePage } from './profile';

/** The wallet panel has its own spec; stubbed so these tests only see profile requests. */
@Component({ selector: 'app-wallet-panel', template: '' })
class StubWalletPanel {}

describe('Profile page', () => {
  const avatars: Avatar[] = [
    { id: 'classic', name: 'Classic', color: '#5b2bd6' },
    { id: 'ocean', name: 'Ocean', color: '#1d4ed8' },
  ];
  const tiers = [
    { id: 'holder', name: 'Holder', minChogs: 1 },
    { id: 'collector', name: 'Collector', minChogs: 3 },
    { id: 'patron', name: 'Patron', minChogs: 5 },
    { id: 'whale', name: 'Whale', minChogs: 10 },
  ];
  const profile: Profile = {
    playerId: 'p-1',
    displayName: 'Chog 4821',
    kind: 'Guest',
    avatar: avatars[0],
    gamesPlayed: 5,
    wins: 2,
    xp: 70,
    collector: { chogsHeld: 0, badge: null, next: tiers[0], tiers },
  };

  const achievements: Achievement[] = [
    { id: 'first_match', name: 'First Match', description: 'Complete a match.', progress: 1, target: 1, unlockedAt: '2026-10-14T12:22:11Z' },
    { id: 'regular', name: 'Regular', description: 'Complete 10 matches.', progress: 5, target: 10, unlockedAt: null },
    { id: 'full_degen', name: 'Full Degen', description: 'Reach a Degen Score of 100 in a match.', progress: 90, target: 100, unlockedAt: null },
  ];

  let fixture: ComponentFixture<ProfilePage>;
  let http: HttpTestingController;
  let session: PlayerSession;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ProfilePage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).overrideComponent(ProfilePage, { remove: { imports: [WalletPanel] }, add: { imports: [StubWalletPanel] } });
    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(PlayerSession);
    session.player.set({ playerId: 'p-1', displayName: 'Chog 4821', kind: 'Guest', walletAddress: null });
    fixture = TestBed.createComponent(ProfilePage);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('reloads when the session switches to another player', async () => {
    load();

    session.player.set({ playerId: 'p-2', displayName: 'Wallet Chog', kind: 'Wallet', walletAddress: '0x' + 'a'.repeat(40) });
    await fixture.whenStable();
    load({ ...profile, playerId: 'p-2', displayName: 'Wallet Chog', kind: 'Wallet' });

    expect(nameInput().value).toBe('Wallet Chog');
  });

  it('does not reload when the same player changes name', async () => {
    load();

    session.updateDisplayName('Renamed');
    await fixture.whenStable();

    http.expectNone('/api/profile');
  });

  it('includes the wallet panel once the profile is loaded', () => {
    load();
    expect(element().querySelector('app-wallet-panel')).not.toBeNull();
  });

  const element = () => fixture.nativeElement as HTMLElement;
  const nameInput = () => element().querySelector('#displayName') as HTMLInputElement;
  const radio = (id: string) => element().querySelector(`input[type="radio"][data-avatar-id="${id}"]`) as HTMLInputElement;

  function load(body: Profile = profile, unlocked: Achievement[] = achievements) {
    http.expectOne('/api/profile').flush(body);
    http.expectOne('/api/profile/avatars').flush(avatars);
    http.expectOne('/api/profile/achievements').flush(unlocked);
    fixture.detectChanges();
  }

  const collectorStatus = () => element().querySelector('[data-collector-status]')?.textContent?.replace(/\s+/g, ' ').trim();

  function typeName(value: string) {
    nameInput().value = value;
    nameInput().dispatchEvent(new Event('input'));
    nameInput().dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  function submit() {
    (element().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  it('shows the profile with avatar, name and server-maintained counters', () => {
    load();

    expect(element().querySelector('.name')?.textContent).toBe('Chog 4821');
    expect(element().querySelector('[data-stat="games"]')?.textContent).toBe('5');
    expect(element().querySelector('[data-stat="wins"]')?.textContent).toBe('2');
    expect(element().querySelector('[data-stat="xp"]')?.textContent).toBe('70');
    expect(nameInput().value).toBe('Chog 4821');
    expect(radio('classic').checked).toBe(true);
    expect(element().querySelectorAll('input[type="radio"]')).toHaveLength(2);
  });

  it('invites a guest to sign in with a wallet for a collector badge, with no frame', () => {
    load();

    expect(element().querySelector('[data-collector-status]')?.textContent).toContain('Sign in with a wallet that holds Chogs');
    expect(element().querySelector('.summary app-collector-chip')).toBeNull();
    expect(element().querySelector('[data-frame]')).toBeNull();
  });

  it('shows a holder their collector badge, frame, Chogs held and the next milestone', () => {
    load({
      ...profile,
      kind: 'Wallet',
      collector: { chogsHeld: 3, badge: { id: 'collector', name: 'Collector' }, next: tiers[2], tiers },
    });

    expect(element().querySelector('.name [data-collector="collector"]')?.textContent).toBe('Collector');
    expect(element().querySelector('.summary [data-frame]')?.getAttribute('data-frame')).toBe('collector');
    expect(collectorStatus()).toBe('3 Chogs held · 2 more for Patron');
    expect(element().querySelector('.collector a')?.getAttribute('href')).toBe('/chogs');
  });

  it('lists every collector tier with the Chogs it takes, marking those reached and the player’s own', () => {
    load({ ...profile, kind: 'Wallet', collector: { chogsHeld: 4, badge: { id: 'collector', name: 'Collector' }, next: tiers[2], tiers } });

    const items = [...element().querySelectorAll<HTMLElement>('[data-tier]')];
    expect(items.map((li) => li.querySelector('app-collector-chip')?.textContent)).toEqual(['Holder', 'Collector', 'Patron', 'Whale']);
    expect(items.map((li) => li.querySelector('.hint')?.textContent)).toEqual(['1 Chog', '3 Chogs', '5 Chogs', '10 Chogs']);
    expect(items.map((li) => li.classList.contains('reached'))).toEqual([true, true, false, false]);
    expect(items.map((li) => li.querySelector('.tier-status')?.textContent ?? null)).toEqual(['Reached', 'Your tier', null, null]);
    expect(element().querySelectorAll('.current')).toHaveLength(1);
  });

  it('shows a guest the tiers to aim for, none reached', () => {
    load();

    const items = [...element().querySelectorAll<HTMLElement>('[data-tier]')];
    expect(items.map((li) => li.dataset['tier'])).toEqual(['holder', 'collector', 'patron', 'whale']);
    expect(element().querySelector('.tiers .reached')).toBeNull();
  });

  it('tells a whale they reached the top tier', () => {
    load({ ...profile, kind: 'Wallet', collector: { chogsHeld: 12, badge: { id: 'whale', name: 'Whale' }, next: null, tiers } });

    expect(collectorStatus()).toBe('12 Chogs held · top tier reached');
  });

  it('tells a wallet without Chogs how far the first milestone is, with no badge', () => {
    load({ ...profile, kind: 'Wallet' });

    expect(collectorStatus()).toBe('0 Chogs held · 1 more for Holder');
    expect(element().querySelector('.summary app-collector-chip')).toBeNull();
  });

  it('lists every achievement with its progress, or when it was unlocked', () => {
    load();

    const items = [...element().querySelectorAll<HTMLElement>('[data-achievement]')];
    expect(items.map((item) => item.dataset['achievement'])).toEqual(['first_match', 'regular', 'full_degen']);
    expect(element().querySelector('[data-unlocked-count]')?.textContent).toBe('1 of 3 unlocked');

    const [firstMatch, regular, fullDegen] = items;
    expect(firstMatch.classList).toContain('unlocked');
    expect(firstMatch.querySelector('[data-unlocked]')?.textContent).toBe('Unlocked Oct 14, 2026');
    expect(firstMatch.querySelector('progress')).toBeNull();
    expect(regular.textContent).toContain('Complete 10 matches.');
    expect(regular.classList).not.toContain('unlocked');
    expect(regular.querySelector('[data-progress]')?.textContent).toBe('5/10');
    const bar = fullDegen.querySelector('progress') as HTMLProgressElement;
    expect([bar.value, bar.max, bar.getAttribute('aria-label')]).toEqual([90, 100, 'Full Degen']);
  });

  it('shows the achievements of the player the session switched to', async () => {
    load();

    session.player.set({ playerId: 'p-2', displayName: 'Wallet Chog', kind: 'Wallet', walletAddress: '0x' + 'a'.repeat(40) });
    await fixture.whenStable();
    load({ ...profile, playerId: 'p-2' }, achievements.map((a) => ({ ...a, progress: 0, unlockedAt: null })));

    expect(element().querySelector('[data-unlocked-count]')?.textContent).toBe('0 of 3 unlocked');
    expect(element().querySelector('[data-unlocked]')).toBeNull();
  });

  it('saves only changed fields, without any player id, and updates the header name', () => {
    load();
    typeName('  Speedy Chog ');
    radio('ocean').click();
    fixture.detectChanges();

    submit();

    const req = http.expectOne({ method: 'PATCH', url: '/api/profile' });
    expect(req.request.body).toEqual({ displayName: 'Speedy Chog', avatarId: 'ocean' });
    req.flush({ ...profile, displayName: 'Speedy Chog', avatar: avatars[1] });
    fixture.detectChanges();

    expect(element().querySelector('.name')?.textContent).toBe('Speedy Chog');
    expect(element().querySelector('[role="status"]')?.textContent).toContain('Profile saved');
    expect(session.player()?.displayName).toBe('Speedy Chog');
  });

  it.each(['ab', 'two  spaces', '<script>', 'x'.repeat(21)])('blocks the invalid name "%s" before sending', (value) => {
    load();
    typeName(value);

    submit();

    http.expectNone('/api/profile');
    expect(element().querySelector('[data-error="displayName"]')).not.toBeNull();
    expect(nameInput().getAttribute('aria-invalid')).toBe('true');
  });

  it('shows validation errors returned by the server and keeps the old profile', () => {
    load();
    typeName('Valid Name');

    submit();
    http
      .expectOne({ method: 'PATCH', url: '/api/profile' })
      .flush({ errors: { displayName: ['Display name must be 3–20 characters.'] } }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(element().querySelector('[data-server-error="displayName"]')?.textContent).toContain('3–20 characters');
    expect(element().querySelector('[role="alert"]')?.textContent).toContain('not saved');
    expect(element().querySelector('.name')?.textContent).toBe('Chog 4821');
    expect(session.player()?.displayName).toBe('Chog 4821');
  });

  it('does not send a request when nothing changed', () => {
    load();

    submit();

    http.expectNone('/api/profile');
    expect(element().querySelector('[role="status"]')?.textContent).toContain('Profile saved');
  });

  describe('photo', () => {
    const imageUrl = '/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10';
    const withPhoto: Profile = { ...profile, avatar: { ...avatars[0], imageUrl } };
    const summaryPhoto = () => element().querySelector('.summary app-avatar-badge img');
    const removeButton = () => element().querySelector('[data-remove-photo]') as HTMLButtonElement | null;
    const photoError = () => element().querySelector('[data-photo-error]')?.textContent?.trim();

    function choose(file: File) {
      const input = element().querySelector('[data-photo-input]') as HTMLInputElement;
      Object.defineProperty(input, 'files', { value: [file], configurable: true });
      input.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    }

    it('offers an upload of JPEG, PNG or WebP and no removal without a photo', () => {
      load();

      expect((element().querySelector('[data-photo-input]') as HTMLInputElement).accept).toBe('image/jpeg,image/png,image/webp');
      expect(element().querySelector('.photo-pick')?.textContent).toContain('Upload photo');
      expect(removeButton()).toBeNull();
      expect(summaryPhoto()).toBeNull();
    });

    it('uploads the chosen file as is, without any player id, and shows the photo', () => {
      load();
      typeName('Unsaved Name');
      const file = new File([new Uint8Array([0xff, 0xd8, 0xff])], 'me.jpg', { type: 'image/jpeg' });

      choose(file);
      expect(element().querySelector('[role="status"]')?.textContent).toContain('Uploading');
      const req = http.expectOne({ method: 'PUT', url: '/api/profile/avatar-image' });
      expect(req.request.body).toBe(file);
      expect(req.request.headers.get('Content-Type')).toBe('image/jpeg');
      req.flush(withPhoto);
      fixture.detectChanges();

      expect(summaryPhoto()?.getAttribute('src')).toBe(imageUrl);
      expect(element().querySelector('.photo-pick')?.textContent).toContain('Replace photo');
      expect(element().querySelector('[data-avatar-under-photo]')).not.toBeNull();
      expect(nameInput().value).toBe('Unsaved Name');
      // The header's avatar follows.
      expect(session.avatar()?.imageUrl).toBe(imageUrl);
    });

    it('removes the photo and shows the built-in avatar again', () => {
      load(withPhoto);
      expect(summaryPhoto()?.getAttribute('src')).toBe(imageUrl);

      (removeButton() as HTMLButtonElement).click();
      fixture.detectChanges();
      http.expectOne({ method: 'DELETE', url: '/api/profile/avatar-image' }).flush(profile);
      fixture.detectChanges();

      expect(summaryPhoto()).toBeNull();
      expect(element().querySelector('.summary app-avatar-badge')?.textContent?.trim()).toBe('C');
      expect(removeButton()).toBeNull();
      expect(session.avatar()?.imageUrl).toBeFalsy();
    });

    it('refuses other file types and oversized files without sending them', () => {
      load();

      choose(new File(['<svg/>'], 'me.svg', { type: 'image/svg+xml' }));
      expect(photoError()).toBe('Choose a JPEG, PNG or WebP photo.');

      choose(new File([new Uint8Array(2 * 1024 * 1024 + 1)], 'big.png', { type: 'image/png' }));
      expect(photoError()).toBe('That photo is too big. Choose one of 2 MB or less.');

      http.expectNone('/api/profile/avatar-image');
    });

    it('explains a refusal by the server and keeps the current photo', () => {
      load(withPhoto);

      choose(new File([new Uint8Array([0xff, 0xd8, 0xff])], 'fake.jpg', { type: 'image/jpeg' }));
      http
        .expectOne({ method: 'PUT', url: '/api/profile/avatar-image' })
        .flush({ code: 'avatar_not_an_image' }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      expect(photoError()).toBe("That file isn't a photo we can read. Choose a JPEG, PNG or WebP photo.");
      expect(summaryPhoto()?.getAttribute('src')).toBe(imageUrl);
    });

    it('asks to wait after too many changes', () => {
      load(withPhoto);

      (removeButton() as HTMLButtonElement).click();
      http.expectOne({ method: 'DELETE', url: '/api/profile/avatar-image' }).flush(null, { status: 429, statusText: 'Too Many Requests' });
      fixture.detectChanges();

      expect(photoError()).toBe('Too many photo changes. Try again in a few minutes.');
      expect(removeButton()?.disabled).toBe(false);
    });
  });

  it('shows an error with retry when the profile cannot be loaded', () => {
    const avatarsRequest = http.expectOne('/api/profile/avatars');
    const achievementsRequest = http.expectOne('/api/profile/achievements');
    http.expectOne('/api/profile').flush(null, { status: 503, statusText: 'Unavailable' });
    expect([avatarsRequest.cancelled, achievementsRequest.cancelled]).toEqual([true, true]);
    fixture.detectChanges();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('could not be loaded');
    (element().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    load();

    expect(element().querySelector('.name')?.textContent).toBe('Chog 4821');
  });
});
