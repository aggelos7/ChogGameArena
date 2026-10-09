import { parseAccount, parseChainId, shortAddress, toHexChainId, walletErrorCode } from './eip1193';
import { ALICE } from './testing';

describe('EIP-1193 helpers', () => {
  it('accepts only a 20-byte hex address as the first account', () => {
    expect(parseAccount([ALICE, 'ignored'])).toBe(ALICE);
    for (const bad of [[], null, 'not-an-array', [42], ['0x123'], ['0x' + 'g'.repeat(40)], ['0x' + 'a'.repeat(41)], [ALICE + ' ']]) {
      expect(parseAccount(bad)).toBeNull();
    }
  });

  it('parses hex chain ids into positive safe integers', () => {
    expect(parseChainId('0x279f')).toBe(10143);
    expect(parseChainId('0x8f')).toBe(143);
    for (const bad of [143, '143', '0x', '0x0', '0xzz', '0x' + 'f'.repeat(14), null, undefined]) {
      expect(parseChainId(bad)).toBeNull();
    }
  });

  it('formats chain ids and addresses', () => {
    expect(toHexChainId(10143)).toBe('0x279f');
    expect(shortAddress(ALICE)).toBe('0x0000…11ce');
  });

  it('reads error codes, including nested ones', () => {
    expect(walletErrorCode({ code: 4001 })).toBe(4001);
    expect(walletErrorCode({ data: { originalError: { code: 4902 } } })).toBe(4902);
    expect(walletErrorCode(new Error('x'))).toBeUndefined();
    expect(walletErrorCode(null)).toBeUndefined();
  });
});
