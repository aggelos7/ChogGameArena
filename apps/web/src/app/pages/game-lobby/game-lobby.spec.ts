import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Room } from '../../rooms/rooms-api';
import { GameLobby } from './game-lobby';

describe('Game lobby page', () => {
  const room: Room = {
    code: 'K7QF3M',
    matchId: '5f0c9a8e-3d4b-4c1a-9e2f-7a6b5c4d3e2f',
    version: 0,
    gameType: 'chog-life',
    status: 'Waiting',
    minPlayers: 2,
    capacity: 6,
    turnNumber: 0,
    turnEndsAt: null,
    turnSeconds: null,
    matchEndsAt: null,
    state: null,
    abilities: [],
    achievements: [],
    members: [
      {
        displayName: 'Alice',
        avatarId: 'classic',
        collector: null,
        isHost: true,
        isYou: true,
        isReady: false,
        character: { kind: 'Standard', id: 'classic', name: 'Classic', color: '#5b2bd6' },
        ability: null,
      hasAbility: false,
        hasLeft: false,
        isTurn: false,
        rank: null,
        result: null,
        seat: null,
      },
    ],
  };

  let fixture: ComponentFixture<GameLobby>;
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [GameLobby],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(GameLobby);
    fixture.componentRef.setInput('game', { id: 'chog-life', name: 'Chog Life', description: '', status: 'Available' });
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const createButton = () => element().querySelector('section button') as HTMLButtonElement;
  const codeInput = () => element().querySelector('#room-code') as HTMLInputElement;
  const submitJoin = () => element().querySelector('form')?.dispatchEvent(new Event('submit'));

  async function typeCode(value: string) {
    codeInput().value = value;
    codeInput().dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('shows the game name with create and join options', () => {
    expect(element().querySelector('h1')?.textContent).toBe('Chog Life');
    expect(createButton().textContent?.trim()).toBe('Create room');
    expect(element().querySelector('label[for="room-code"]')?.textContent).toBe('Room code');
  });

  it('creates a room for this game and opens it', () => {
    createButton().click();
    fixture.detectChanges();

    const req = http.expectOne('/api/rooms');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ gameType: 'chog-life' });
    expect(createButton().disabled).toBe(true);
    req.flush(room);

    expect(navigate).toHaveBeenCalledWith(['/rooms', 'K7QF3M']);
  });

  it('links to the room the player is already in', () => {
    createButton().click();
    http.expectOne('/api/rooms').flush(
      { code: 'already_in_room', roomCode: 'ABCDEF', detail: 'You are already in a room.' },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    const error = element().querySelector('[data-error="create"]') as HTMLElement;
    expect(error.textContent).toContain('You are already in a room.');
    expect(error.querySelector('a')?.getAttribute('href')).toBe('/rooms/ABCDEF');
    expect(createButton().disabled).toBe(false);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('goes to the invite link for a valid code, in any case', async () => {
    await typeCode(' k7qf3m ');
    submitJoin();

    expect(navigate).toHaveBeenCalledWith(['/join', 'K7QF3M']);
  });

  it('refuses a malformed code without calling the server', async () => {
    await typeCode('K7QF0');
    submitJoin();
    fixture.detectChanges();

    expect(element().querySelector('[data-error="code"]')?.textContent).toContain('six-character room code');
    expect(codeInput().getAttribute('aria-invalid')).toBe('true');
    expect(navigate).not.toHaveBeenCalled();
  });
});
