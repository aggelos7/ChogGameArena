import { TestBed } from '@angular/core/testing';
import { toProviderDetail, WalletDiscovery } from './wallet-discovery';
import { FakeWallet } from './testing';

const announce = (detail: unknown) => window.dispatchEvent(new CustomEvent('eip6963:announceProvider', { detail }));
const info = (overrides: Record<string, unknown> = {}) => ({
  uuid: 'u-1',
  name: 'Rabby',
  rdns: 'io.rabby',
  icon: 'data:image/svg+xml;base64,PHN2Zy8+',
  ...overrides,
});

describe('WalletDiscovery', () => {
  it.each([
    'data:image/svg+xml;base64,PHN2Zy8+',
    'data:image/svg+xml,%3Csvg%3E%3C/svg%3E',
    'data:image/svg+xml;charset=utf-8,<svg/>',
    'data:image/png;base64,iVBORw0KGgo=',
    'data:image/x-icon;base64,AAABAA==',
    'DATA:IMAGE/WEBP;base64,UklGRg==',
    'https://wallet.example/icon.png',
  ])('keeps the wallet icon %s', (icon) => {
    expect(toProviderDetail({ info: info({ icon }), provider: new FakeWallet() })?.info.icon).toBe(icon);
  });

  it('trims whitespace around an icon', () => {
    expect(toProviderDetail({ info: info({ icon: '\n data:image/png;base64,iVBORw0KGgo= ' }), provider: new FakeWallet() })?.info.icon).toBe(
      'data:image/png;base64,iVBORw0KGgo=',
    );
  });

  afterEach(() => delete (window as Window & { ethereum?: unknown }).ethereum);

  it('requests and collects EIP-6963 announcements, once per wallet', () => {
    const requested = vi.fn((event: Event) => {
      announce({ info: info(), provider: new FakeWallet() });
      announce({ info: info(), provider: new FakeWallet() });
      announce({ info: info({ uuid: 'u-2', name: 'Phantom', rdns: 'app.phantom' }), provider: new FakeWallet() });
      return event;
    });
    window.addEventListener('eip6963:requestProvider', requested);
    const discovery = TestBed.inject(WalletDiscovery);

    discovery.start();
    discovery.start();
    window.removeEventListener('eip6963:requestProvider', requested);

    expect(requested).toHaveBeenCalledOnce();
    expect(discovery.wallets().map((w) => w.info.name)).toEqual(['Rabby', 'Phantom']);
  });

  it('falls back to window.ethereum only when no wallet announces itself', () => {
    const legacy = new FakeWallet();
    (window as Window & { ethereum?: unknown }).ethereum = legacy;
    const discovery = TestBed.inject(WalletDiscovery);

    discovery.start();
    expect(discovery.wallets()).toEqual([{ info: expect.objectContaining({ name: 'Browser wallet' }), provider: legacy }]);

    announce({ info: info(), provider: new FakeWallet() });
    expect(discovery.wallets().map((w) => w.info.name)).toEqual(['Rabby']);
  });

  it('finds nothing when there is no wallet', () => {
    const discovery = TestBed.inject(WalletDiscovery);
    discovery.start();
    expect(discovery.wallets()).toEqual([]);
  });

  it('ignores malformed announcements and unsafe icons', () => {
    const provider = new FakeWallet();
    for (const bad of [null, {}, { info: info() }, { info: info(), provider: {} }, { info: info({ name: '' }), provider }, { info: info({ uuid: 7 }), provider }]) {
      expect(toProviderDetail(bad)).toBeNull();
    }
    for (const unsafe of ['javascript:alert(1)', 'http://wallet.example/x.png', 'data:text/html,<script>', 42]) {
      expect(toProviderDetail({ info: info({ icon: unsafe }), provider })?.info.icon).toBe('');
    }
    expect(toProviderDetail({ info: info({ name: 'x'.repeat(100) }), provider })?.info.name).toHaveLength(40);
  });
});
