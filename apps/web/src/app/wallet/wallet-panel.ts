import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { EmptyState, ErrorState, LoadingState } from '../shared/ui/state-views';
import { shortAddress } from './eip1193';
import { WalletChainApi } from './wallet-chain';
import { WalletConnection } from './wallet-connection';
import { WalletDiscovery } from './wallet-discovery';

/** Connect / show / switch network / disconnect for a browser wallet (STORY-012). */
@Component({
  selector: 'app-wallet-panel',
  imports: [LoadingState, ErrorState, EmptyState],
  templateUrl: './wallet-panel.html',
  styleUrl: './wallet-panel.scss',
})
export class WalletPanel implements OnInit {
  private readonly chainApi = inject(WalletChainApi);
  protected readonly discovery = inject(WalletDiscovery);
  protected readonly wallet = inject(WalletConnection);

  protected readonly chainState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly connected = computed(() => {
    const state = this.wallet.state();
    return state.status === 'connected' ? state : null;
  });
  protected readonly shortAddress = shortAddress;

  ngOnInit(): void {
    this.discovery.start();
    this.loadChain();
  }

  protected loadChain(): void {
    this.chainState.set('loading');
    this.chainApi.get().subscribe({
      next: (chain) => {
        this.wallet.chain.set(chain);
        this.chainState.set('ready');
      },
      error: () => this.chainState.set('error'),
    });
  }
}
