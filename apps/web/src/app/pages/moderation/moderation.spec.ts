import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReportedPhoto } from '../../moderation/moderation-api';
import { ModerationPage } from './moderation';

describe('Reported photos page (DEC-070)', () => {
  const alice: ReportedPhoto = {
    imageId: '0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10',
    imageUrl: '/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10',
    ownerName: 'Alice',
    reporters: 3,
    reasons: { hate: 2, other: 1 },
    firstReportedAt: '2026-10-19T10:00:00Z',
    lastReportedAt: '2026-10-19T11:00:00Z',
    hidden: true,
  };
  const bob: ReportedPhoto = {
    ...alice,
    imageId: '1c9f4b63-7f2d-4e1f-8b62-4f4b2c8d3e21',
    imageUrl: '/api/avatar-images/1c9f4b63-7f2d-4e1f-8b62-4f4b2c8d3e21',
    ownerName: 'Bob',
    reporters: 1,
    reasons: { scam: 1 },
    hidden: false,
  };

  let fixture: ComponentFixture<ModerationPage>;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [ModerationPage], providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ModerationPage);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const item = (photo: ReportedPhoto) => element().querySelector<HTMLElement>(`[data-report="${photo.imageId}"]`);
  const button = (photo: ReportedPhoto, action: 'remove' | 'dismiss') =>
    element().querySelector(`[data-report="${photo.imageId}"] [data-${action}]`) as HTMLButtonElement;

  function load(photos: ReportedPhoto[] = [alice, bob]) {
    http.expectOne('/api/moderation/reports').flush(photos);
    fixture.detectChanges();
  }

  it('lists each reported photo with its owner, counts, reasons and whether it is hidden', () => {
    load();

    const first = item(alice) as HTMLElement;
    expect(first.querySelector('img')?.getAttribute('src')).toBe(alice.imageUrl);
    expect(first.querySelector('.owner')?.textContent).toContain('Alice');
    expect(first.querySelector('[data-hidden]')).not.toBeNull();
    expect(first.querySelector('[data-reporters]')?.textContent?.trim()).toBe('3 reports');
    expect([...first.querySelectorAll('[data-reason]')].map((r) => r.textContent?.trim())).toEqual([
      'Hate or harassment: 2',
      'Something else: 1',
    ]);
    expect(item(bob)?.querySelector('[data-hidden]')).toBeNull();
    expect(item(bob)?.querySelector('[data-reporters]')?.textContent?.trim()).toBe('1 report');
  });

  it('removes a photo and takes it off the list', () => {
    load();

    button(alice, 'remove').click();
    http.expectOne({ method: 'DELETE', url: `/api/moderation/photos/${alice.imageId}` }).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(item(alice)).toBeNull();
    expect(item(bob)).not.toBeNull();
  });

  it('dismisses the reports and takes the photo off the list; the last one leaves an empty list', () => {
    load([bob]);

    button(bob, 'dismiss').click();
    http.expectOne({ method: 'POST', url: `/api/moderation/photos/${bob.imageId}/dismiss` }).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(element().querySelector('[data-no-reports]')).not.toBeNull();
  });

  it('keeps the photo listed and says so when an action fails', () => {
    load();

    button(alice, 'remove').click();
    http.expectOne(`/api/moderation/photos/${alice.imageId}`).flush(null, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(element().querySelector('[data-action-error]')?.textContent).toContain('not removed');
    expect(button(alice, 'remove').disabled).toBe(false);
  });

  it('shows nothing to a player who is not an operator', () => {
    http.expectOne('/api/moderation/reports').flush(null, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(element().querySelector('[data-not-operator]')).not.toBeNull();
    expect(element().querySelector('.reports')).toBeNull();
  });
});
