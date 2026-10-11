import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Room } from '../../rooms/rooms-api';
import { CATALOGUE } from '../../testing';
import { RoomPage } from './room';

describe('Room page', () => {
  const room: Room = {
    code: 'K7QF3M',
    gameType: 'chog-life',
    status: 'Waiting',
    capacity: 6,
    members: [
      { displayName: 'Alice', avatarId: 'classic', isHost: true, isYou: false },
      { displayName: 'Bob', avatarId: 'ocean', isHost: false, isYou: true },
    ],
  };

  let fixture: ComponentFixture<RoomPage>;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RoomPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RoomPage);
    fixture.componentRef.setInput('code', 'k7qf3m');
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const members = () =>
    Array.from(element().querySelectorAll('[data-list="members"] li')).map((li) =>
      Array.from(li.querySelectorAll('span'))
        .map((span) => span.textContent?.trim())
        .join(' '),
    );
  const button = (label: string) =>
    Array.from(element().querySelectorAll('button')).find((b) => b.textContent?.trim() === label) as HTMLButtonElement;

  function load(body: Room = room) {
    http.expectOne('/api/rooms/K7QF3M').flush(body);
    http.expectOne('/api/games').flush(CATALOGUE);
    fixture.detectChanges();
  }

  it('shows the code, invite link and members with host and you marked', () => {
    load();

    expect(element().querySelector('h1')?.textContent).toBe('Chog Life room');
    expect(element().querySelector('[data-room-code]')?.textContent).toBe('K7QF3M');
    expect(element().querySelector('[data-invite-link]')?.textContent).toBe(`${location.origin}/join/K7QF3M`);
    expect(element().querySelector('#players-heading')?.textContent).toBe('Players 2/6');
    expect(members()).toEqual(['Alice Host', 'Bob You']);
  });

  it('copies the invite link', async () => {
    load();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    button('Copy invite link').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith(`${location.origin}/join/K7QF3M`);
    expect(element().querySelector('.copy-status')?.textContent?.trim()).toBe('Link copied.');
  });

  it('refreshes to show players who joined', () => {
    load();

    button('Refresh').click();
    http.expectOne('/api/rooms/K7QF3M').flush({
      ...room,
      members: [...room.members, { displayName: 'Carol', avatarId: 'gold', isHost: false, isYou: false }],
    });
    fixture.detectChanges();

    expect(members()).toEqual(['Alice Host', 'Bob You', 'Carol']);
  });

  it('tells a non-member the room is not theirs to see', () => {
    http.expectOne('/api/rooms/K7QF3M').flush({ code: 'room_not_found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(element().querySelector('[data-error="room"]')?.textContent).toContain('you’re not in it');
    expect(element().querySelector('[data-list="members"]')).toBeNull();
  });

  it('offers a retry when the room cannot be loaded', () => {
    http.expectOne('/api/rooms/K7QF3M').flush('down', { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    button('Try again').click();
    load();

    expect(members()).toHaveLength(2);
  });
});
