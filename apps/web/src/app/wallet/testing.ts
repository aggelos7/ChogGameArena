import { Eip1193Provider, WalletProviderDetail } from './eip1193';
import { WalletChain } from './wallet-chain';

export const ALICE = '0x00000000000000000000000000000000000a11ce';
export const BOB = '0x0000000000000000000000000000000000000b0b';

export const TESTNET: WalletChain = {
  chainId: 10143,
  name: 'Monad Testnet',
  nativeCurrency: { name: 'Monad', symbol: 'MON', decimals: 18 },
  rpcUrls: ['https://testnet-rpc.monad.xyz'],
  blockExplorerUrls: ['https://testnet.monadvision.com'],
};

type Listener = (...args: unknown[]) => void;

/** In-memory EIP-1193 wallet. Override `handlers[method]` to return odd data or throw. */
export class FakeWallet implements Eip1193Provider {
  accounts: unknown = [ALICE];
  chainId = '0x279f'; // 10143
  readonly knownChains = new Set(['0x279f', '0x1']);
  readonly calls: { method: string; params?: unknown[] }[] = [];
  readonly handlers: Record<string, (params?: unknown[]) => unknown> = {};
  private readonly listeners = new Map<string, Set<Listener>>();

  async request({ method, params }: { method: string; params?: unknown[] }): Promise<unknown> {
    this.calls.push({ method, params });
    const handler = this.handlers[method];
    if (handler) return handler(params);
    switch (method) {
      case 'eth_requestAccounts':
      case 'eth_accounts':
        return this.accounts;
      case 'eth_chainId':
        return this.chainId;
      case 'wallet_switchEthereumChain': {
        const chainId = (params?.[0] as { chainId: string }).chainId;
        if (!this.knownChains.has(chainId)) throw { code: 4902, message: 'Unrecognized chain' };
        this.chainId = chainId;
        this.emit('chainChanged', chainId);
        return null;
      }
      case 'wallet_addEthereumChain': {
        const chainId = (params?.[0] as { chainId: string }).chainId;
        this.knownChains.add(chainId);
        this.chainId = chainId;
        this.emit('chainChanged', chainId);
        return null;
      }
      case 'wallet_revokePermissions':
        return null;
      default:
        throw { code: 4200, message: `Unsupported method ${method}` };
    }
  }

  on(event: string, listener: Listener): void {
    if (!this.listeners.has(event)) this.listeners.set(event, new Set());
    this.listeners.get(event)?.add(listener);
  }

  removeListener(event: string, listener: Listener): void {
    this.listeners.get(event)?.delete(listener);
  }

  listenerCount(): number {
    return [...this.listeners.values()].reduce((total, set) => total + set.size, 0);
  }

  emit(event: string, ...args: unknown[]): void {
    for (const listener of [...(this.listeners.get(event) ?? [])]) listener(...args);
  }

  methods(): string[] {
    return this.calls.map((call) => call.method);
  }
}

export function walletDetail(provider: Eip1193Provider, name = 'Fake Wallet'): WalletProviderDetail {
  return { info: { uuid: `uuid-${name}`, name, icon: '', rdns: `test.${name.toLowerCase().replace(/\s/g, '')}` }, provider };
}

/** A promise you resolve or reject from the test, for "request still open in the wallet" cases. */
export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
