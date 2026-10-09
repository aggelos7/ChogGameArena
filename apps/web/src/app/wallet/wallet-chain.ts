import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** Mirrors `WalletChainResponse` from `GET /api/wallet/chain`. */
export interface WalletChain {
  chainId: number;
  name: string;
  nativeCurrency: { name: string; symbol: string; decimals: number };
  rpcUrls: string[];
  blockExplorerUrls: string[];
}

@Injectable({ providedIn: 'root' })
export class WalletChainApi {
  private readonly http = inject(HttpClient);

  get(): Observable<WalletChain> {
    return this.http.get<WalletChain>('/api/wallet/chain');
  }
}
