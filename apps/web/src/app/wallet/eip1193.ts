/** Minimal EIP-1193 provider surface used by the app (https://eips.ethereum.org/EIPS/eip-1193). */
export interface Eip1193Provider {
  request(args: { method: string; params?: unknown[] }): Promise<unknown>;
  on?(event: string, listener: (...args: unknown[]) => void): void;
  removeListener?(event: string, listener: (...args: unknown[]) => void): void;
}

/** EIP-6963 wallet metadata (https://eips.ethereum.org/EIPS/eip-6963). */
export interface WalletInfo {
  uuid: string;
  name: string;
  /** `data:image/...` URI, or empty when the wallet supplied none (or an unsafe one). */
  icon: string;
  rdns: string;
}

export interface WalletProviderDetail {
  info: WalletInfo;
  provider: Eip1193Provider;
}

/** EIP-1193 / EIP-3326 error codes the UI distinguishes. */
export const WalletErrorCode = {
  UserRejected: 4001,
  UnrecognizedChain: 4902,
  RequestPending: -32002,
} as const;

const ADDRESS = /^0x[0-9a-fA-F]{40}$/;
const HEX_QUANTITY = /^0x[0-9a-fA-F]{1,13}$/;

/** First account as a valid 20-byte hex address, or null. Wallet responses are untrusted input. */
export function parseAccount(accounts: unknown): string | null {
  if (!Array.isArray(accounts) || accounts.length === 0) return null;
  const [first] = accounts;
  return typeof first === 'string' && ADDRESS.test(first) ? first : null;
}

/** Hex chain id (e.g. `0x279f`) as a positive safe integer, or null. */
export function parseChainId(value: unknown): number | null {
  if (typeof value !== 'string' || !HEX_QUANTITY.test(value)) return null;
  const chainId = Number.parseInt(value, 16);
  return Number.isSafeInteger(chainId) && chainId > 0 ? chainId : null;
}

export function toHexChainId(chainId: number): string {
  return `0x${chainId.toString(16)}`;
}

/** Error code from a provider error, including wallets that nest it (e.g. some mobile in-app browsers). */
export function walletErrorCode(error: unknown): number | undefined {
  const candidate = error as { code?: unknown; data?: { originalError?: { code?: unknown } } } | null;
  const code = candidate?.code ?? candidate?.data?.originalError?.code;
  return typeof code === 'number' ? code : undefined;
}

/** A 65-byte r ‖ s ‖ v signature as returned by `personal_sign`, or null. */
export function parseSignature(value: unknown): string | null {
  return typeof value === 'string' && /^0x[0-9a-fA-F]{130}$/.test(value) ? value : null;
}

/** `personal_sign` takes the message as 0x-hex UTF-8 bytes; wallets show it decoded. */
export function utf8ToHex(text: string): string {
  return `0x${Array.from(new TextEncoder().encode(text), (byte) => byte.toString(16).padStart(2, '0')).join('')}`;
}

export function shortAddress(address: string): string {
  return `${address.slice(0, 6)}…${address.slice(-4)}`;
}
