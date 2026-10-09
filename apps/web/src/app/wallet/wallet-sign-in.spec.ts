import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { CurrentPlayer, PlayerSession } from '../identity/player-session';
import { ALICE, FakeWallet, SIGNATURE, TESTNET, deferred, walletDetail } from './testing';
import { WalletConnection } from './wallet-connection';
import { WalletSignIn } from './wallet-sign-in';

describe('WalletSignIn', () => {
  const guest: CurrentPlayer = { playerId: 'p-1', displayName: 'Alice', kind: 'Guest', walletAddress: null };
  const signedIn: CurrentPlayer = { ...guest, kind: 'Wallet', walletAddress: ALICE };
  const challenge = { challengeId: 'c-1', message: 'arena wants you to sign in…', expiresAt: '2026-10-10T12:05:00Z' };

  let http: HttpTestingController;
  let wallet: FakeWallet;
  let connection: WalletConnection;
  let session: PlayerSession;
  let signIn: WalletSignIn;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(PlayerSession);
    session.setPlayer(guest);
    connection = TestBed.inject(WalletConnection);
    connection.chain.set(TESTNET);
    signIn = TestBed.inject(WalletSignIn);
    wallet = new FakeWallet();
    await connection.connect(walletDetail(wallet));
  });

  afterEach(() => http.verify());

  const tick = () => new Promise((resolve) => setTimeout(resolve));

  async function respondChallenge() {
    await tick();
    const request = http.expectOne({ method: 'POST', url: '/api/wallet/challenge' });
    expect(request.request.body).toEqual({ address: ALICE });
    request.flush(challenge);
    await tick();
  }

  it('signs the server message and adopts the identity the server returns', async () => {
    const done = signIn.signIn();
    expect(signIn.busy()).toBe('signing-in');
    await respondChallenge();

    const verify = http.expectOne({ method: 'POST', url: '/api/wallet/verify' });
    expect(verify.request.body).toEqual({ challengeId: 'c-1', signature: SIGNATURE });
    expect(session.player()).toEqual(guest);
    verify.flush(signedIn);
    await done;

    expect(session.player()).toEqual(signedIn);
    expect(signIn.signedInWithConnectedWallet()).toBe(true);
    expect(signIn.busy()).toBeNull();
    expect(wallet.calls.find((c) => c.method === 'personal_sign')?.params?.[1]).toBe(ALICE);
  });

  it('does not sign in on an unsupported chain', async () => {
    wallet.emit('chainChanged', '0x1');

    await signIn.signIn();

    http.expectNone('/api/wallet/challenge');
    expect(session.player()).toEqual(guest);
  });

  it('stays a guest and explains when signing is cancelled', async () => {
    wallet.handlers['personal_sign'] = () => Promise.reject({ code: 4001 });

    const done = signIn.signIn();
    await respondChallenge();
    await done;

    http.expectNone('/api/wallet/verify');
    expect(session.player()).toEqual(guest);
    expect(signIn.error()).toBe('Signing was cancelled in your wallet.');
  });

  it('shows the server reason when verification fails', async () => {
    const done = signIn.signIn();
    await respondChallenge();
    http.expectOne('/api/wallet/verify').flush(
      { errors: { challengeId: ['This sign-in request expired or was already used. Start again.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await done;

    expect(session.player()).toEqual(guest);
    expect(signIn.error()).toBe('This sign-in request expired or was already used. Start again.');
  });

  it('explains rate limiting', async () => {
    const done = signIn.signIn();
    await tick();
    http.expectOne('/api/wallet/challenge').flush(null, { status: 429, statusText: 'Too Many Requests' });
    await done;

    expect(signIn.error()).toBe('Too many sign-in attempts. Wait a minute and try again.');
  });

  it('ignores a second click while signing', async () => {
    const signing = deferred<unknown>();
    wallet.handlers['personal_sign'] = () => signing.promise;

    const first = signIn.signIn();
    await signIn.signIn();
    await respondChallenge();
    signing.reject({ code: 4001 });
    await first;

    expect(wallet.calls.filter((c) => c.method === 'personal_sign')).toHaveLength(1);
  });

  it('sign-out ends the server session, disconnects the wallet and continues as a new guest', async () => {
    session.setPlayer(signedIn);
    const newGuest: CurrentPlayer = { playerId: 'p-9', displayName: 'Chog 1234', kind: 'Guest', walletAddress: null };

    const done = signIn.signOut();
    http.expectOne({ method: 'POST', url: '/api/wallet/sign-out' }).flush(null, { status: 204, statusText: 'No Content' });
    await tick();
    http.expectOne('/api/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    await tick();
    http.expectOne('/api/guest-sessions').flush(newGuest);
    await done;

    expect(session.player()).toEqual(newGuest);
    expect(connection.state()).toEqual({ status: 'disconnected' });
    expect(wallet.methods()).toContain('wallet_revokePermissions');
  });

  it('keeps the wallet session when sign-out fails', async () => {
    session.setPlayer(signedIn);

    const done = signIn.signOut();
    http.expectOne('/api/wallet/sign-out').flush(null, { status: 500, statusText: 'Error' });
    await done;

    expect(session.player()).toEqual(signedIn);
    expect(connection.state().status).toBe('connected');
    expect(signIn.error()).toBe('Sign-out failed. Check your connection and try again.');
  });
});
