import { Injectable, computed, signal } from '@angular/core';
import {
  Eip1193Provider,
  WalletErrorCode,
  WalletInfo,
  WalletProviderDetail,
  parseAccount,
  parseChainId,
  toHexChainId,
  walletErrorCode,
} from './eip1193';
import { WalletChain } from './wallet-chain';

export type WalletState =
  | { status: 'disconnected' }
  | { status: 'connecting'; wallet: WalletInfo }
  | { status: 'connected'; wallet: WalletInfo; address: string; chainId: number };

class InvalidWalletResponse extends Error {}

function errorMessage(error: unknown, action: 'connect' | 'switch'): string {
  if (error instanceof InvalidWalletResponse) return 'Your wallet returned an unexpected response. Try again.';
  switch (walletErrorCode(error)) {
    case WalletErrorCode.UserRejected:
      return action === 'connect' ? 'Connection was cancelled in your wallet.' : 'Network switch was cancelled in your wallet.';
    case WalletErrorCode.RequestPending:
      return 'Your wallet already has a request open. Check your wallet.';
    default:
      return action === 'connect' ? 'Could not connect to your wallet.' : 'Could not switch network in your wallet.';
  }
}

/**
 * Browser-side wallet connection (STORY-012). A connected address is only what the wallet reports:
 * it is display state, never proof of ownership or authentication (ADR-005, SUITE-003).
 * Every value from the wallet is validated; anything unexpected leaves or returns the app to
 * `disconnected` rather than showing an unverified address or chain.
 */
@Injectable({ providedIn: 'root' })
export class WalletConnection {
  private readonly stateSignal = signal<WalletState>({ status: 'disconnected' });
  private provider: Eip1193Provider | null = null;
  /** Incremented on every connect/disconnect, so late wallet responses from an abandoned attempt are ignored. */
  private attempt = 0;

  readonly state = this.stateSignal.asReadonly();
  readonly error = signal<string | null>(null);
  readonly switching = signal(false);
  /** The supported network from the server; set by the wallet panel once loaded. */
  readonly chain = signal<WalletChain | null>(null);

  readonly onSupportedChain = computed(() => {
    const state = this.state();
    const chain = this.chain();
    return state.status === 'connected' && chain !== null && state.chainId === chain.chainId;
  });

  async connect({ info, provider }: WalletProviderDetail): Promise<void> {
    if (this.state().status === 'connecting') return;
    this.reset();
    const attempt = ++this.attempt;
    this.error.set(null);
    this.stateSignal.set({ status: 'connecting', wallet: info });

    try {
      const address = parseAccount(await provider.request({ method: 'eth_requestAccounts' }));
      const chainId = parseChainId(await provider.request({ method: 'eth_chainId' }));
      if (attempt !== this.attempt) return;
      if (address === null || chainId === null) throw new InvalidWalletResponse();

      this.provider = provider;
      provider.on?.('accountsChanged', this.onAccountsChanged);
      provider.on?.('chainChanged', this.onChainChanged);
      provider.on?.('disconnect', this.onDisconnect);
      this.stateSignal.set({ status: 'connected', wallet: info, address, chainId });
    } catch (error) {
      if (attempt !== this.attempt) return;
      this.stateSignal.set({ status: 'disconnected' });
      this.error.set(errorMessage(error, 'connect'));
    }
  }

  /** Asks the wallet to switch to the configured chain, adding it first if the wallet does not know it. */
  async switchToSupportedChain(): Promise<void> {
    const chain = this.chain();
    const provider = this.provider;
    if (chain === null || provider === null || this.state().status !== 'connected' || this.switching()) return;

    const attempt = this.attempt;
    this.switching.set(true);
    this.error.set(null);
    try {
      try {
        await provider.request({ method: 'wallet_switchEthereumChain', params: [{ chainId: toHexChainId(chain.chainId) }] });
      } catch (error) {
        if (walletErrorCode(error) !== WalletErrorCode.UnrecognizedChain) throw error;
        await provider.request({
          method: 'wallet_addEthereumChain',
          params: [
            {
              chainId: toHexChainId(chain.chainId),
              chainName: chain.name,
              nativeCurrency: chain.nativeCurrency,
              rpcUrls: chain.rpcUrls,
              blockExplorerUrls: chain.blockExplorerUrls,
            },
          ],
        });
      }
      // Trust only what the wallet reports afterwards; some wallets do not emit `chainChanged`.
      const chainId = parseChainId(await provider.request({ method: 'eth_chainId' }));
      if (attempt !== this.attempt) return;
      if (chainId === null) throw new InvalidWalletResponse();
      this.updateChain(chainId);
    } catch (error) {
      if (attempt === this.attempt) this.error.set(errorMessage(error, 'switch'));
    } finally {
      if (attempt === this.attempt) this.switching.set(false);
    }
  }

  /**
   * Forgets the wallet in this app and asks the wallet to revoke the site's account permission
   * (best effort; not every wallet supports it).
   */
  disconnect(): void {
    const provider = this.provider;
    this.reset();
    this.error.set(null);
    provider?.request({ method: 'wallet_revokePermissions', params: [{ eth_accounts: {} }] }).catch(() => undefined);
  }

  private reset(): void {
    this.attempt++;
    this.provider?.removeListener?.('accountsChanged', this.onAccountsChanged);
    this.provider?.removeListener?.('chainChanged', this.onChainChanged);
    this.provider?.removeListener?.('disconnect', this.onDisconnect);
    this.provider = null;
    this.switching.set(false);
    this.stateSignal.set({ status: 'disconnected' });
  }

  private updateChain(chainId: number): void {
    this.stateSignal.update((state) => (state.status === 'connected' ? { ...state, chainId } : state));
  }

  private readonly onAccountsChanged = (accounts: unknown) => {
    const address = parseAccount(accounts);
    if (address === null) {
      // Locked wallet, revoked permission or malformed data: show nothing rather than a stale address.
      this.reset();
      return;
    }
    this.stateSignal.update((state) => (state.status === 'connected' ? { ...state, address } : state));
  };

  private readonly onChainChanged = (value: unknown) => {
    const chainId = parseChainId(value);
    if (chainId === null) {
      this.reset();
      return;
    }
    this.updateChain(chainId);
  };

  private readonly onDisconnect = () => this.reset();
}
