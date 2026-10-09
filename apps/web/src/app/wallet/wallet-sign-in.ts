import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CurrentPlayer, PlayerSession } from '../identity/player-session';
import { WalletErrorCode, walletErrorCode } from './eip1193';
import { InvalidWalletResponse, WalletChangedError, WalletConnection } from './wallet-connection';

interface ChallengeResponse {
  challengeId: string;
  message: string;
  expiresAt: string;
}

function errorMessage(error: unknown): string {
  if (error instanceof WalletChangedError) return 'Your wallet changed account while signing. Try again.';
  if (error instanceof InvalidWalletResponse) return 'Your wallet returned an unexpected response. Try again.';
  if (error instanceof HttpErrorResponse) {
    if (error.status === 429) return 'Too many sign-in attempts. Wait a minute and try again.';
    const errors = error.status === 400 ? (error.error?.errors as Record<string, string[]> | undefined) : undefined;
    const first = errors && Object.values(errors).flat()[0];
    return first ?? 'Sign-in failed. Try again.';
  }
  switch (walletErrorCode(error)) {
    case WalletErrorCode.UserRejected:
      return 'Signing was cancelled in your wallet.';
    case WalletErrorCode.RequestPending:
      return 'Your wallet already has a request open. Check your wallet.';
    default:
      return 'Sign-in failed. Try again.';
  }
}

/**
 * Wallet sign-in (STORY-013): the server issues a one-time message, the wallet signs it, the server verifies
 * the signature and replaces the session. The browser never decides who is signed in; it adopts the
 * identity the server returns.
 */
@Injectable({ providedIn: 'root' })
export class WalletSignIn {
  private readonly http = inject(HttpClient);
  private readonly session = inject(PlayerSession);
  private readonly wallet = inject(WalletConnection);

  readonly busy = signal<'signing-in' | 'signing-out' | null>(null);
  readonly error = signal<string | null>(null);

  /** Address of the wallet the server session is bound to, if any. */
  readonly signedInAddress = computed(() => this.session.player()?.walletAddress ?? null);

  /** The connected wallet account is the one this session is signed in with. */
  readonly signedInWithConnectedWallet = computed(() => {
    const state = this.wallet.state();
    const signedIn = this.signedInAddress();
    return state.status === 'connected' && signedIn !== null && state.address.toLowerCase() === signedIn;
  });

  async signIn(): Promise<void> {
    const state = this.wallet.state();
    if (this.busy() || state.status !== 'connected' || !this.wallet.onSupportedChain()) return;

    this.busy.set('signing-in');
    this.error.set(null);
    try {
      const challenge = await firstValueFrom(
        this.http.post<ChallengeResponse>('/api/wallet/challenge', { address: state.address }),
      );
      const signature = await this.wallet.signMessage(challenge.message);
      const player = await firstValueFrom(
        this.http.post<CurrentPlayer>('/api/wallet/verify', { challengeId: challenge.challengeId, signature }),
      );
      this.session.setPlayer(player);
    } catch (error) {
      this.error.set(errorMessage(error));
    } finally {
      this.busy.set(null);
    }
  }

  /** Ends the wallet session on the server and forgets the wallet; the app continues as a new guest. */
  async signOut(): Promise<void> {
    if (this.busy()) return;
    this.busy.set('signing-out');
    this.error.set(null);
    try {
      await this.session.signOut();
      this.wallet.disconnect();
    } catch {
      this.error.set('Sign-out failed. Check your connection and try again.');
    } finally {
      this.busy.set(null);
    }
  }
}
