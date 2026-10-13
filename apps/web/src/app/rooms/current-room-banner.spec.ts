import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { CurrentPlayer, PlayerSession } from '../identity/player-session';
import { CurrentRoomBanner } from './current-room-banner';
import { Room, RoomStatus } from './rooms-api';

@Component({ template: '' })
class StubPage {}

const room = (status: RoomStatus, code = 'K7QF3M'): Room => ({
  code,
  matchId: '5f0c9a8e-3d4b-4c1a-9e2f-7a6b5c4d3e2f',
  version: 4,
  gameType: 'chog-life',
  status,
  minPlayers: 2,
  capacity: 6,
  turnNumber: status === 'Active' ? 1 : 0,
  turnEndsAt: null,
  members: [],
  state: null,
  abilities: [],
});

const guest = (displayName: string): CurrentPlayer => ({ playerId: displayName, displayName, kind: 'Guest', walletAddress: null });

describe('CurrentRoomBanner', () => {
  let fixture: ComponentFixture<CurrentRoomBanner>;
  let http: HttpTestingController;
  const player = signal<CurrentPlayer | null>(null);

  beforeEach(() => {
    player.set(guest('Alice'));
    TestBed.configureTestingModule({
      imports: [CurrentRoomBanner],
      providers: [
        provideRouter([
          { path: '', component: StubPage },
          { path: 'games', component: StubPage },
          { path: 'rooms/:code', component: StubPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: PlayerSession, useValue: { player } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const banner = () => element().querySelector('[data-current-room]');
  const text = () => banner()?.textContent?.replace(/\s+/g, ' ').trim();

  async function render() {
    fixture = TestBed.createComponent(CurrentRoomBanner);
    await fixture.whenStable();
  }

  /** Answers the open-room check: a room, or `204` for none. */
  async function answer(current: Room | null) {
    http.expectOne('/api/rooms/current').flush(current, current ? undefined : { status: 204, statusText: 'No Content' });
    await fixture.whenStable();
  }

  async function navigate(url: string) {
    await TestBed.inject(Router).navigateByUrl(url);
    await fixture.whenStable();
  }

  it('brings the player back to a game that is still on', async () => {
    await render();
    await answer(room('Active'));

    expect(text()).toBe('Your game in room K7QF3M is still on. Return to game');
    expect(banner()?.querySelector('a')?.getAttribute('href')).toBe('/rooms/K7QF3M');
  });

  it('brings the player back to a room waiting to start', async () => {
    await render();
    await answer(room('Waiting'));

    expect(text()).toBe('You’re in room K7QF3M, waiting to start. Back to room');
  });

  it('shows nothing when the player has no open room or the check fails', async () => {
    await render();
    await answer(null);
    expect(banner()).toBeNull();

    await navigate('/games');
    http.expectOne('/api/rooms/current').flush({ code: 'unknown' }, { status: 500, statusText: 'Error' });
    await fixture.whenStable();
    expect(banner()).toBeNull();
  });

  it('waits for a session before asking', async () => {
    player.set(null);
    await render();
    http.expectNone('/api/rooms/current');

    player.set(guest('Alice'));
    await fixture.whenStable();
    await answer(room('Active'));
    expect(banner()).not.toBeNull();
  });

  it('hides on the room’s own page and checks again after each navigation', async () => {
    await render();
    await answer(room('Active'));

    await navigate('/rooms/k7qf3m');
    await answer(room('Active'));
    expect(banner()).toBeNull();

    // Another room's page still offers the way back.
    await navigate('/rooms/ZZZZZZ');
    await answer(room('Active'));
    expect(banner()).not.toBeNull();

    // The game ended (or the player left) meanwhile.
    await navigate('/games');
    await answer(null);
    expect(banner()).toBeNull();
  });

  it('checks again when the player changes, such as after a wallet sign-in', async () => {
    await render();
    await answer(null);

    player.set(guest('Bob'));
    await fixture.whenStable();
    await answer(room('Waiting', 'ABCDEF'));

    expect(banner()?.querySelector('a')?.getAttribute('href')).toBe('/rooms/ABCDEF');
  });
});
