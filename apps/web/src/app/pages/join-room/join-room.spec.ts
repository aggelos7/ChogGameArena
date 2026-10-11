import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { JoinRoomPage } from './join-room';

describe('Join room page', () => {
  let fixture: ComponentFixture<JoinRoomPage>;
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.spyOn>;

  function open(code: string) {
    TestBed.configureTestingModule({
      imports: [JoinRoomPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(JoinRoomPage);
    fixture.componentRef.setInput('code', code);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;

  it('joins the room from the link and opens it in place of the link', () => {
    open('k7qf3m');

    expect(element().querySelector('h1')?.textContent).toBe('Join room K7QF3M');
    expect(element().querySelector('[role="status"]')?.textContent).toContain('Joining room…');
    const req = http.expectOne('/api/rooms/K7QF3M/join');
    expect(req.request.method).toBe('POST');
    req.flush({ code: 'K7QF3M', gameType: 'chog-life', status: 'Waiting', capacity: 6, members: [] });

    expect(navigate).toHaveBeenCalledWith(['/rooms', 'K7QF3M'], { replaceUrl: true });
  });

  it.each([
    ['room_full', 409, 'This room is full.'],
    ['room_started', 409, 'This room’s game has already started.'],
    ['room_closed', 409, 'This room is closed.'],
    ['room_not_found', 404, 'No room with this code was found.'],
  ])('explains %s', (code, status, message) => {
    open('K7QF3M');
    http.expectOne('/api/rooms/K7QF3M/join').flush({ code }, { status, statusText: 'Error' });
    fixture.detectChanges();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain(message);
    expect(element().querySelector('a.back')?.getAttribute('href')).toBe('/games');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('links to the room the player is already in', () => {
    open('K7QF3M');
    http.expectOne('/api/rooms/K7QF3M/join').flush({ code: 'already_in_room', roomCode: 'ABCDEF' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(element().querySelector('[role="alert"] a')?.getAttribute('href')).toBe('/rooms/ABCDEF');
  });
});
