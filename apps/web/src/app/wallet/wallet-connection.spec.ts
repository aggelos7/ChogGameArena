import { TestBed } from '@angular/core/testing';
import { WalletConnection } from './wallet-connection';
import { ALICE, BOB, FakeWallet, SIGNATURE, TESTNET, deferred, walletDetail } from './testing';
import { WalletChangedError } from './wallet-connection';

describe('WalletConnection', () => {
  let wallet: FakeWallet;
  let connection: WalletConnection;

  beforeEach(() => {
    wallet = new FakeWallet();
    connection = TestBed.inject(WalletConnection);
    connection.chain.set(TESTNET);
  });

  const connect = () => connection.connect(walletDetail(wallet));

  it('connects and shows the address and chain the wallet reports', async () => {
    await connect();

    expect(connection.state()).toEqual({
      status: 'connected',
      wallet: expect.objectContaining({ name: 'Fake Wallet' }),
      address: ALICE,
      chainId: 10143,
    });
    expect(connection.onSupportedChain()).toBe(true);
    expect(wallet.methods()).toEqual(['eth_requestAccounts', 'eth_chainId']);
  });

  it('flags an unsupported chain', async () => {
    wallet.chainId = '0x1';
    await connect();

    expect(connection.state()).toMatchObject({ status: 'connected', chainId: 1 });
    expect(connection.onSupportedChain()).toBe(false);
  });

  it('switches to the supported chain', async () => {
    wallet.chainId = '0x1';
    await connect();

    await connection.switchToSupportedChain();

    expect(wallet.calls).toContainEqual({ method: 'wallet_switchEthereumChain', params: [{ chainId: '0x279f' }] });
    expect(connection.onSupportedChain()).toBe(true);
    expect(connection.error()).toBeNull();
  });

  it('adds the chain with the server configuration when the wallet does not know it', async () => {
    wallet.chainId = '0x1';
    wallet.knownChains.delete('0x279f');
    await connect();

    await connection.switchToSupportedChain();

    expect(wallet.calls).toContainEqual({
      method: 'wallet_addEthereumChain',
      params: [
        {
          chainId: '0x279f',
          chainName: 'Monad Testnet',
          nativeCurrency: { name: 'Monad', symbol: 'MON', decimals: 18 },
          rpcUrls: ['https://testnet-rpc.monad.xyz'],
          blockExplorerUrls: ['https://testnet.monadvision.com'],
        },
      ],
    });
    expect(connection.onSupportedChain()).toBe(true);
  });

  it('keeps the unsupported chain and explains when the user rejects the switch', async () => {
    wallet.chainId = '0x1';
    wallet.handlers['wallet_switchEthereumChain'] = () => Promise.reject({ code: 4001 });
    await connect();

    await connection.switchToSupportedChain();

    expect(connection.state()).toMatchObject({ status: 'connected', chainId: 1 });
    expect(connection.onSupportedChain()).toBe(false);
    expect(connection.error()).toBe('Network switch was cancelled in your wallet.');
    expect(connection.switching()).toBe(false);
  });

  it('trusts the chain the wallet reports after a switch, not the one requested', async () => {
    wallet.chainId = '0x1';
    wallet.handlers['wallet_switchEthereumChain'] = () => null; // claims success but stays put
    await connect();

    await connection.switchToSupportedChain();

    expect(connection.onSupportedChain()).toBe(false);
  });

  it('follows account and chain changes from the wallet', async () => {
    await connect();

    wallet.emit('accountsChanged', [BOB]);
    wallet.emit('chainChanged', '0x1');

    expect(connection.state()).toMatchObject({ status: 'connected', address: BOB, chainId: 1 });
    expect(connection.onSupportedChain()).toBe(false);
  });

  it.each([
    ['accountsChanged', []],
    ['accountsChanged', ['not-an-address']],
    ['chainChanged', 'garbage'],
    ['disconnect', { code: 4900 }],
  ])('drops back to disconnected on %s with %j', async (event, payload) => {
    await connect();

    wallet.emit(event, payload);

    expect(connection.state()).toEqual({ status: 'disconnected' });
    expect(wallet.listenerCount()).toBe(0);
  });

  it('disconnect clears the wallet state, stops listening and revokes the permission', async () => {
    await connect();

    connection.disconnect();

    expect(connection.state()).toEqual({ status: 'disconnected' });
    expect(wallet.listenerCount()).toBe(0);
    expect(wallet.methods()).toContain('wallet_revokePermissions');

    wallet.emit('accountsChanged', [BOB]);
    expect(connection.state()).toEqual({ status: 'disconnected' });
  });

  it('disconnect still succeeds when the wallet cannot revoke permissions', async () => {
    wallet.handlers['wallet_revokePermissions'] = () => Promise.reject({ code: 4200 });
    await connect();

    connection.disconnect();
    await Promise.resolve();

    expect(connection.state()).toEqual({ status: 'disconnected' });
    expect(connection.error()).toBeNull();
  });

  it.each([
    ['user rejection', () => Promise.reject({ code: 4001 }), 'Connection was cancelled in your wallet.'],
    ['a pending request', () => Promise.reject({ code: -32002 }), 'Your wallet already has a request open. Check your wallet.'],
    ['no accounts', () => [], 'Your wallet returned an unexpected response. Try again.'],
    ['a malformed address', () => ['0x1234'], 'Your wallet returned an unexpected response. Try again.'],
    ['an unknown failure', () => Promise.reject(new Error('boom')), 'Could not connect to your wallet.'],
  ])('stays disconnected on %s', async (_, handler, message) => {
    wallet.handlers['eth_requestAccounts'] = handler;

    await connect();

    expect(connection.state()).toEqual({ status: 'disconnected' });
    expect(connection.error()).toBe(message);
    expect(wallet.listenerCount()).toBe(0);
  });

  it('stays disconnected when the wallet reports a malformed chain id', async () => {
    wallet.chainId = 'mainnet';

    await connect();

    expect(connection.state()).toEqual({ status: 'disconnected' });
    expect(connection.error()).toContain('unexpected response');
  });

  it('ignores a wallet approval that arrives after the user cancelled', async () => {
    const approval = deferred<unknown>();
    wallet.handlers['eth_requestAccounts'] = () => approval.promise;
    const pending = connect();
    expect(connection.state().status).toBe('connecting');

    connection.disconnect();
    approval.resolve([ALICE]);
    await pending;

    expect(connection.state()).toEqual({ status: 'disconnected' });
    expect(wallet.listenerCount()).toBe(0);
  });

  it('is not on a supported chain until the server chain is known', async () => {
    connection.chain.set(null);
    await connect();

    expect(connection.onSupportedChain()).toBe(false);
  });

  it('signs a message with personal_sign as hex UTF-8 for the connected account', async () => {
    await connect();

    const signature = await connection.signMessage('Hi Χ');

    expect(signature).toBe(SIGNATURE);
    expect(wallet.calls[wallet.calls.length - 1]).toEqual({ method: 'personal_sign', params: ['0x486920cea7', ALICE] });
  });

  it('rejects a malformed signature from the wallet', async () => {
    wallet.handlers['personal_sign'] = () => '0x1234';
    await connect();

    await expect(connection.signMessage('hello')).rejects.toThrow();
  });

  it('rejects a signature if the account changed while the wallet was open', async () => {
    const signing = deferred<unknown>();
    wallet.handlers['personal_sign'] = () => signing.promise;
    await connect();

    const pending = connection.signMessage('hello');
    wallet.emit('accountsChanged', [BOB]);
    signing.resolve(SIGNATURE);

    await expect(pending).rejects.toBeInstanceOf(WalletChangedError);
  });

  it('cannot sign without a connected wallet', async () => {
    await expect(connection.signMessage('hello')).rejects.toBeInstanceOf(WalletChangedError);
  });
});
