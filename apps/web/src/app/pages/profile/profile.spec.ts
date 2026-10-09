import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PlayerSession } from '../../identity/player-session';
import { Avatar, Profile } from '../../profile/profile-api';
import { ProfilePage } from './profile';

describe('Profile page', () => {
  const avatars: Avatar[] = [
    { id: 'classic', name: 'Classic', color: '#5b2bd6' },
    { id: 'ocean', name: 'Ocean', color: '#1d4ed8' },
  ];
  const profile: Profile = {
    playerId: 'p-1',
    displayName: 'Chog 4821',
    kind: 'Guest',
    avatar: avatars[0],
    gamesPlayed: 5,
    wins: 2,
  };

  let fixture: ComponentFixture<ProfilePage>;
  let http: HttpTestingController;
  let session: PlayerSession;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ProfilePage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    session = TestBed.inject(PlayerSession);
    session.player.set({ playerId: 'p-1', displayName: 'Chog 4821', kind: 'Guest' });
    fixture = TestBed.createComponent(ProfilePage);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const nameInput = () => element().querySelector('#displayName') as HTMLInputElement;
  const radio = (id: string) => element().querySelector(`input[type="radio"][data-avatar-id="${id}"]`) as HTMLInputElement;

  function load(body: Profile = profile) {
    http.expectOne('/api/profile').flush(body);
    http.expectOne('/api/profile/avatars').flush(avatars);
    fixture.detectChanges();
  }

  function typeName(value: string) {
    nameInput().value = value;
    nameInput().dispatchEvent(new Event('input'));
    nameInput().dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  function submit() {
    (element().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  it('shows the profile with avatar, name and server-maintained counters', () => {
    load();

    expect(element().querySelector('.name')?.textContent).toBe('Chog 4821');
    expect(element().querySelector('[data-stat="games"]')?.textContent).toBe('5');
    expect(element().querySelector('[data-stat="wins"]')?.textContent).toBe('2');
    expect(nameInput().value).toBe('Chog 4821');
    expect(radio('classic').checked).toBe(true);
    expect(element().querySelectorAll('input[type="radio"]')).toHaveLength(2);
  });

  it('saves only changed fields, without any player id, and updates the header name', () => {
    load();
    typeName('  Speedy Chog ');
    radio('ocean').click();
    fixture.detectChanges();

    submit();

    const req = http.expectOne({ method: 'PATCH', url: '/api/profile' });
    expect(req.request.body).toEqual({ displayName: 'Speedy Chog', avatarId: 'ocean' });
    req.flush({ ...profile, displayName: 'Speedy Chog', avatar: avatars[1] });
    fixture.detectChanges();

    expect(element().querySelector('.name')?.textContent).toBe('Speedy Chog');
    expect(element().querySelector('[role="status"]')?.textContent).toContain('Profile saved');
    expect(session.player()?.displayName).toBe('Speedy Chog');
  });

  it.each(['ab', 'two  spaces', '<script>', 'x'.repeat(21)])('blocks the invalid name "%s" before sending', (value) => {
    load();
    typeName(value);

    submit();

    http.expectNone('/api/profile');
    expect(element().querySelector('[data-error="displayName"]')).not.toBeNull();
    expect(nameInput().getAttribute('aria-invalid')).toBe('true');
  });

  it('shows validation errors returned by the server and keeps the old profile', () => {
    load();
    typeName('Valid Name');

    submit();
    http
      .expectOne({ method: 'PATCH', url: '/api/profile' })
      .flush({ errors: { displayName: ['Display name must be 3–20 characters.'] } }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(element().querySelector('[data-server-error="displayName"]')?.textContent).toContain('3–20 characters');
    expect(element().querySelector('[role="alert"]')?.textContent).toContain('not saved');
    expect(element().querySelector('.name')?.textContent).toBe('Chog 4821');
    expect(session.player()?.displayName).toBe('Chog 4821');
  });

  it('does not send a request when nothing changed', () => {
    load();

    submit();

    http.expectNone('/api/profile');
    expect(element().querySelector('[role="status"]')?.textContent).toContain('Profile saved');
  });

  it('shows an error with retry when the profile cannot be loaded', () => {
    const avatarsRequest = http.expectOne('/api/profile/avatars');
    http.expectOne('/api/profile').flush(null, { status: 503, statusText: 'Unavailable' });
    expect(avatarsRequest.cancelled).toBe(true);
    fixture.detectChanges();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('could not be loaded');
    (element().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    load();

    expect(element().querySelector('.name')?.textContent).toBe('Chog 4821');
  });
});
