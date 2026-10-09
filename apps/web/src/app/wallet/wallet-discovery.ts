import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { Eip1193Provider, WalletInfo, WalletProviderDetail } from './eip1193';

const INJECTED: WalletInfo = { uuid: 'injected', name: 'Browser wallet', icon: '', rdns: 'injected' };

function isProvider(value: unknown): value is Eip1193Provider {
  return typeof (value as Eip1193Provider | null)?.request === 'function';
}

function text(value: unknown, max: number): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim().slice(0, max) : null;
}

/**
 * EIP-6963 requires a `data:` URI, but wallets differ (MIME types, whitespace, hosted images). Any image
 * data URI or HTTPS URL is kept: it is only used as an `<img src>`, which cannot run script. Anything
 * else (`javascript:`, `http:`, non-image data) is dropped.
 */
function safeIcon(value: unknown): string {
  if (typeof value !== 'string') return '';
  const icon = value.trim();
  return /^data:image\/[a-z0-9.+-]+[;,]/i.test(icon) || /^https:\/\//i.test(icon) ? icon : '';
}

/** Validates an EIP-6963 announcement; page scripts can dispatch anything on `window`. */
export function toProviderDetail(detail: unknown): WalletProviderDetail | null {
  const candidate = detail as { info?: Record<string, unknown>; provider?: unknown } | null;
  const uuid = text(candidate?.info?.['uuid'], 64);
  const name = text(candidate?.info?.['name'], 40);
  const rdns = text(candidate?.info?.['rdns'], 100);
  if (!uuid || !name || !rdns || !isProvider(candidate?.provider)) return null;
  return { info: { uuid, name, rdns, icon: safeIcon(candidate.info?.['icon']) }, provider: candidate.provider };
}

/**
 * Finds browser wallets via EIP-6963 announcements. Falls back to the legacy `window.ethereum`
 * provider only when no wallet announces itself.
 */
@Injectable({ providedIn: 'root' })
export class WalletDiscovery {
  private readonly window = inject(DOCUMENT).defaultView;
  private readonly announced = signal<WalletProviderDetail[]>([]);
  private readonly legacy = signal<WalletProviderDetail | null>(null);
  private started = false;

  readonly wallets = computed(() => {
    const announced = this.announced();
    const legacy = this.legacy();
    return announced.length > 0 || legacy === null ? announced : [legacy];
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.window?.removeEventListener('eip6963:announceProvider', this.onAnnounce));
  }

  /** Idempotent. */
  start(): void {
    if (this.started || !this.window) return;
    this.started = true;
    this.window.addEventListener('eip6963:announceProvider', this.onAnnounce);
    this.window.dispatchEvent(new Event('eip6963:requestProvider'));
    const ethereum = (this.window as Window & { ethereum?: unknown }).ethereum;
    if (isProvider(ethereum)) {
      this.legacy.set({ info: INJECTED, provider: ethereum });
    }
  }

  private readonly onAnnounce = (event: Event) => {
    const detail = toProviderDetail((event as CustomEvent<unknown>).detail);
    if (!detail) return;
    this.announced.update((wallets) =>
      wallets.some((wallet) => wallet.info.uuid === detail.info.uuid) ? wallets : [...wallets, detail],
    );
  };
}
