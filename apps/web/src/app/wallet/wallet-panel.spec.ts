import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { WalletProviderDetail } from './eip1193';
import { ALICE, FakeWallet, TESTNET, walletDetail } from './testing';
import { WalletDiscovery } from './wallet-discovery';
import { WalletPanel } from './wallet-panel';

describe('Wallet panel', () => {
  let fixture: ComponentFixture<WalletPanel>;
  let http: HttpTestingController;
  let wallet: FakeWallet;
  const wallets = signal<WalletProviderDetail[]>([]);

  beforeEach(() => {
    wallet = new FakeWallet();
    wallets.set([walletDetail(wallet, 'Rabby')]);
    TestBed.configureTestingModule({
      imports: [WalletPanel],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: WalletDiscovery, useValue: { start: vi.fn(), wallets } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(WalletPanel);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const button = (text: string) =>
    Array.from(element().querySelectorAll('button')).find((b) => b.textContent?.includes(text)) as HTMLButtonElement;

  async function settle() {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function loadChain() {
    http.expectOne('/api/wallet/chain').flush(TESTNET);
    fixture.detectChanges();
  }

  it('loads the supported network from the server, with a retry on failure', () => {
    expect(element().querySelector('[role="status"]')?.textContent).toContain('Loading wallet network');

    http.expectOne('/api/wallet/chain').flush('error', { status: 500, statusText: 'Error' });
    fixture.detectChanges();
    expect(element().querySelector('[role="alert"]')?.textContent).toContain('could not be loaded');

    button('Try again').click();
    loadChain();
    expect(button('Connect Rabby')).toBeTruthy();
  });

  it('shows the wallet icon, or a letter badge when the wallet has none', () => {
    wallets.set([
      { ...walletDetail(new FakeWallet(), 'Rabby'), info: { uuid: 'r', name: 'Rabby', rdns: 'io.rabby', icon: 'data:image/png;base64,iVBORw0KGgo=' } },
      walletDetail(new FakeWallet(), 'Phantom'),
    ]);
    loadChain();

    expect(button('Connect Rabby').querySelector('img')?.getAttribute('src')).toBe('data:image/png;base64,iVBORw0KGgo=');
    expect(button('Connect Phantom').querySelector('img')).toBeNull();
    expect(button('Connect Phantom').querySelector('.icon-fallback')?.textContent).toBe('P');
  });

  it('explains what to do when no wallet is installed', () => {
    wallets.set([]);
    loadChain();

    expect(element().textContent).toContain('No wallet found in this browser.');
    expect(element().querySelector('button')).toBeNull();
  });

  it('connects and shows wallet, short address (full for screen readers) and network', async () => {
    loadChain();

    button('Connect Rabby').click();
    await settle();

    const address = element().querySelector('[data-field="address"]') as HTMLElement;
    expect(address.getAttribute('title')).toBe(ALICE);
    expect(address.querySelector('[aria-hidden="true"]')?.textContent).toBe('0x0000…11ce');
    expect(address.querySelector('.visually-hidden')?.textContent).toBe(ALICE);
    expect(element().querySelector('[data-field="network"]')?.textContent?.trim()).toBe('Monad Testnet');
    expect(element().querySelector('.wrong-network')).toBeNull();
  });

  it('prompts a network switch on an unsupported chain', async () => {
    wallet.chainId = '0x1';
    loadChain();
    button('Connect Rabby').click();
    await settle();

    const alert = element().querySelector('.wrong-network[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('Switch to Monad Testnet to continue');
    expect(element().querySelector('[data-field="network"]')?.textContent?.trim()).toBe('Unsupported network (chain 1)');

    button('Switch to Monad Testnet').click();
    await settle();

    expect(element().querySelector('.wrong-network')).toBeNull();
    expect(element().querySelector('[data-field="network"]')?.textContent?.trim()).toBe('Monad Testnet');
  });

  it('disconnect returns to the connect options and clears the address', async () => {
    loadChain();
    button('Connect Rabby').click();
    await settle();

    button('Disconnect').click();
    await settle();

    expect(element().textContent).not.toContain(ALICE);
    expect(button('Connect Rabby')).toBeTruthy();
  });

  it('shows wallet errors as an alert and stays disconnected', async () => {
    wallet.handlers['eth_requestAccounts'] = () => Promise.reject({ code: 4001 });
    loadChain();

    button('Connect Rabby').click();
    await settle();

    expect(element().querySelector('p.error[role="alert"]')?.textContent).toBe('Connection was cancelled in your wallet.');
    expect(button('Connect Rabby')).toBeTruthy();
  });
});
