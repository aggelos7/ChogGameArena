import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { CurrentPlayer, PlayerSession } from './player-session';

describe('PlayerSession', () => {
  const guest: CurrentPlayer = { playerId: '6f1c4a1e-0000-4000-8000-000000000001', displayName: 'Chog 4821', kind: 'Guest', walletAddress: null };
  let session: PlayerSession;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    session = TestBed.inject(PlayerSession);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const flushNext = () => new Promise((resolve) => setTimeout(resolve));

  it('resumes the existing session without creating a new guest', async () => {
    const done = session.ensureGuest();
    expect(session.status()).toBe('loading');

    http.expectOne({ method: 'GET', url: '/api/me' }).flush(guest);
    await done;

    expect(session.player()).toEqual(guest);
    expect(session.status()).toBe('ready');
  });

  it('creates a guest only when the server reports no session', async () => {
    const done = session.ensureGuest();

    http.expectOne('/api/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    await flushNext();
    const create = http.expectOne({ method: 'POST', url: '/api/guest-sessions' });
    expect(create.request.body).toEqual({});
    create.flush(guest);
    await done;

    expect(session.player()).toEqual(guest);
    expect(session.status()).toBe('ready');
  });

  it('never sends a player id to the server', async () => {
    const done = session.ensureGuest();

    const me = http.expectOne('/api/me');
    expect(me.request.headers.keys()).toEqual([]);
    expect(me.request.params.keys()).toEqual([]);
    me.flush(null, { status: 401, statusText: 'Unauthorized' });
    await flushNext();
    http.expectOne('/api/guest-sessions').flush(guest);
    await done;
  });

  it('does not create a guest when the server fails for another reason', async () => {
    const done = session.ensureGuest();

    http.expectOne('/api/me').flush(null, { status: 503, statusText: 'Unavailable' });
    await done;

    http.expectNone('/api/guest-sessions');
    expect(session.player()).toBeNull();
    expect(session.status()).toBe('error');
  });

  it('reports an error without throwing when guest creation fails', async () => {
    const done = session.ensureGuest();

    http.expectOne('/api/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    await flushNext();
    http.expectOne('/api/guest-sessions').flush(null, { status: 429, statusText: 'Too Many Requests' });

    await expect(done).resolves.toBeUndefined();
    expect(session.status()).toBe('error');
  });

  it('signs out on the server, then continues as a new guest', async () => {
    session.setPlayer({ ...guest, kind: 'Wallet', walletAddress: '0x' + 'a'.repeat(40) });
    const newGuest: CurrentPlayer = { ...guest, playerId: 'new' };

    const done = session.signOut();
    http.expectOne({ method: 'POST', url: '/api/wallet/sign-out' }).flush(null, { status: 204, statusText: 'No Content' });
    await flushNext();
    http.expectOne('/api/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    await flushNext();
    http.expectOne('/api/guest-sessions').flush(newGuest);
    await done;

    expect(session.player()).toEqual(newGuest);
  });
});
