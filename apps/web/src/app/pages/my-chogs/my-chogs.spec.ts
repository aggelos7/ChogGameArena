import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Characters } from '../../characters/characters-api';
import { PlayerSession } from '../../identity/player-session';
import { MyChogsPage } from './my-chogs';

describe('My Chogs page', () => {
  const standard = [
    { id: 'classic', name: 'Classic', color: '#5b2bd6' },
    { id: 'ocean', name: 'Ocean', color: '#1d4ed8' },
  ];
  const holder: Characters = {
    nftStatus: 'Available',
    nfts: [
      {
        tokenId: '1',
        name: 'CHOG #1462',
        imageUrl: 'https://gateway.example.test/ipfs/bafy/1462.webp',
        traits: [
          { type: 'Background', value: 'Purple' },
          { type: 'Eyes', value: 'Laser' },
        ],
      },
      { tokenId: '7', name: 'Chog #7', imageUrl: null, traits: [] },
    ],
    standard,
    selected: { kind: 'Standard', id: 'classic' },
  };
  const guest: Characters = { nftStatus: 'NoWallet', nfts: [], standard, selected: { kind: 'Standard', id: 'classic' } };

  let fixture: ComponentFixture<MyChogsPage>;
  let http: HttpTestingController;
  let session: PlayerSession;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [MyChogsPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(PlayerSession);
    session.player.set({ playerId: 'p-1', displayName: 'Alice', kind: 'Wallet', walletAddress: '0x' + 'a'.repeat(40) });
    fixture = TestBed.createComponent(MyChogsPage);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const nftCards = () => Array.from(element().querySelectorAll<HTMLElement>('[data-list="nfts"] li'));
  const standardCards = () => Array.from(element().querySelectorAll<HTMLElement>('[data-list="standard"] li'));
  const card = (selector: string) => element().querySelector(selector) as HTMLElement;
  const radio = (selector: string) => card(selector).querySelector('input[type="radio"]') as HTMLInputElement;
  const inUse = () => Array.from(element().querySelectorAll('.in-use')).map((badge) => badge.closest('li')?.querySelector('h3')?.textContent);
  const status = () => element().querySelector('[role="status"], [role="alert"]')?.textContent?.trim();
  const saveButton = () => element().querySelector('button.save') as HTMLButtonElement;

  function load(body: Characters = holder) {
    http.expectOne('/api/characters').flush(body);
    fixture.detectChanges();
  }

  function choose(selector: string) {
    radio(selector).click();
    fixture.detectChanges();
  }

  function save() {
    saveButton().click();
    fixture.detectChanges();
  }

  it('shows a loading state, then every verified NFT with image, token id and traits', () => {
    expect(status()).toContain('Loading your Chogs');

    load();

    expect(nftCards().map((c) => c.querySelector('h3')?.textContent)).toEqual(['CHOG #1462', 'Chog #7']);
    const first = card('[data-token-id="1"]');
    expect(first.querySelector('img')?.getAttribute('src')).toBe('https://gateway.example.test/ipfs/bafy/1462.webp');
    expect(first.querySelector('img')?.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(first.querySelector('.token')?.textContent).toBe('Token #1');
    expect(Array.from(first.querySelectorAll('.traits div')).map((d) => [d.querySelector('dt')?.textContent, d.querySelector('dd')?.textContent])).toEqual([
      ['Background', 'Purple'],
      ['Eyes', 'Laser'],
    ]);
  });

  it('shows a placeholder and "No traits" when metadata has neither', () => {
    load();

    const bare = card('[data-token-id="7"]');
    expect(bare.querySelector('img')).toBeNull();
    expect(bare.querySelector('[data-placeholder]')).not.toBeNull();
    expect(bare.querySelector('.hint')?.textContent).toBe('No traits');
  });

  it('falls back to the placeholder when an image fails to load', () => {
    load();

    card('[data-token-id="1"] img').dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(card('[data-token-id="1"]').querySelector('img')).toBeNull();
    expect(card('[data-token-id="1"]').querySelector('[data-placeholder]')).not.toBeNull();
  });

  it('always offers the standard Chogs and marks the saved choice as in use', () => {
    load();

    expect(standardCards().map((c) => c.querySelector('h3')?.textContent)).toEqual(['Classic', 'Ocean']);
    expect(inUse()).toEqual(['Classic']);
    expect(radio('[data-standard-id="classic"]').checked).toBe(true);
  });

  it('gives each choice an accessible name that identifies the Chog', () => {
    load();

    expect(radio('[data-token-id="1"]').closest('label')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Choose CHOG #1462, token 1',
    );
  });

  it('asks a guest to sign in with a wallet, and still lets them choose a standard Chog', () => {
    load(guest);

    expect(element().textContent).toContain('Sign in with your wallet to see the Chog NFTs you own.');
    expect(element().querySelector('a[href="/profile"]')).not.toBeNull();
    expect(nftCards()).toHaveLength(0);
    expect(standardCards()).toHaveLength(2);
  });

  it('tells a wallet without NFTs to choose a standard Chog', () => {
    load({ ...holder, nfts: [] });

    expect(element().textContent).toContain('This wallet holds no Chog NFTs.');
    expect(standardCards()).toHaveLength(2);
  });

  it('keeps standard Chogs available when ownership cannot be read, and retries', () => {
    load({ ...holder, nftStatus: 'Unavailable', nfts: [] });

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('cannot be loaded right now');
    expect(standardCards()).toHaveLength(2);

    (element().querySelector('app-error-state button') as HTMLButtonElement).click();
    load();
    expect(nftCards()).toHaveLength(2);
  });

  it('saves the picked NFT and marks it as in use', () => {
    load();

    choose('[data-token-id="7"]');
    expect(card('[data-token-id="7"]').classList).toContain('picked');
    expect(inUse()).toEqual(['Classic']);
    save();

    const request = http.expectOne('/api/characters/selected');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ kind: 'Nft', id: '7' });
    expect(saveButton().disabled).toBe(true);
    request.flush({ kind: 'Nft', id: '7' });
    fixture.detectChanges();

    expect(inUse()).toEqual(['Chog #7']);
    expect(status()).toBe('Your choice is saved.');
  });

  it('does not send a request when the choice is unchanged', () => {
    load();

    save();

    http.expectNone('/api/characters/selected');
    expect(status()).toBe('Your choice is saved.');
  });

  it("shows the server's reason when a choice is refused and keeps the saved one", () => {
    load();

    choose('[data-token-id="1"]');
    save();
    http
      .expectOne('/api/characters/selected')
      .flush({ errors: { id: ['Your wallet does not own this Chog.'] } }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(status()).toBe('Your wallet does not own this Chog.');
    expect(inUse()).toEqual(['Classic']);
    expect(saveButton().disabled).toBe(false);
  });

  it('explains when ownership cannot be checked', () => {
    load();

    choose('[data-token-id="1"]');
    save();
    http.expectOne('/api/characters/selected').flush(null, { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    expect(status()).toBe('NFT ownership cannot be checked right now. Try again shortly.');
  });

  it('shows an error with retry when the gallery cannot be loaded', () => {
    http.expectOne('/api/characters').flush(null, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(status()).toContain('Your Chogs could not be loaded.');
    (element().querySelector('app-error-state button') as HTMLButtonElement).click();
    load();
    expect(nftCards()).toHaveLength(2);
  });

  it('reloads when the session switches to another player', async () => {
    load();

    session.player.set({ playerId: 'p-2', displayName: 'Guest', kind: 'Guest', walletAddress: null });
    await fixture.whenStable();
    load(guest);

    expect(nftCards()).toHaveLength(0);
    expect(element().textContent).toContain('Sign in with your wallet');
  });

  it('does not reload when the same player changes name', async () => {
    load();

    session.updateDisplayName('Renamed');
    await fixture.whenStable();

    http.expectNone('/api/characters');
  });
});
