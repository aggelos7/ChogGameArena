import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReportPhoto } from './report-photo';

describe('Report photo (DEC-070)', () => {
  const photoUrl = '/api/avatar-images/0b8f3a52-6f1c-4d0e-9a51-3f3a1b7c2d10';
  let fixture: ComponentFixture<ReportPhoto>;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [ReportPhoto], providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ReportPhoto);
    fixture.componentRef.setInput('photoUrl', photoUrl);
    fixture.componentRef.setInput('name', 'Alice');
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const element = () => fixture.nativeElement as HTMLElement;
  const reportButton = () => element().querySelector<HTMLButtonElement>('[data-report]');
  const reason = () => element().querySelector('[data-report-reason]') as HTMLSelectElement;
  const text = (selector: string) => element().querySelector(selector)?.textContent?.trim();

  function open() {
    (element().querySelector('[data-report]') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  function send(value: string) {
    reason().value = value;
    (element().querySelector('[data-send-report]') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  it('starts as a button naming whose photo it reports', () => {
    expect(reportButton()?.getAttribute('aria-label')).toBe('Report Alice’s photo');
    expect(element().querySelector('form')).toBeNull();
  });

  it('offers only the fixed reasons, with no free text', () => {
    open();

    expect([...reason().options].map((o) => o.value)).toEqual(['sexual', 'hate', 'violence', 'scam', 'other']);
    expect(element().querySelector('textarea, input[type="text"]')).toBeNull();
  });

  it('sends the chosen reason to the photo and then says it was reported', () => {
    open();
    send('violence');

    const req = http.expectOne({ method: 'POST', url: `${photoUrl}/reports` });
    expect(req.request.body).toEqual({ reason: 'violence' });
    req.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(text('[data-reported]')).toContain('Reported');
    expect(reportButton()).toBeNull();
  });

  it('says so when the player already reported the photo', () => {
    open();
    send('scam');
    http.expectOne(`${photoUrl}/reports`).flush({ code: 'photo_already_reported' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(text('[data-reported]')).toBe('You have already reported this photo.');
  });

  it('keeps the form open to try again after a failure or too many reports', () => {
    open();
    send('hate');
    http.expectOne(`${photoUrl}/reports`).flush(null, { status: 429, statusText: 'Too Many Requests' });
    fixture.detectChanges();
    expect(text('[data-report-error]')).toBe('Too many reports. Try again in a few minutes.');

    send('hate');
    http.expectOne(`${photoUrl}/reports`).flush(null, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(text('[data-report-error]')).toBe('Your report was not sent. Try again.');
    expect(element().querySelector('[data-send-report]')).not.toBeNull();
  });

  it('closes without sending on cancel', () => {
    open();
    (element().querySelector('[data-cancel-report]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(reportButton()).not.toBeNull();
    http.expectNone(`${photoUrl}/reports`);
  });

  it('never sends a report anywhere but a photo the API serves', () => {
    fixture.componentRef.setInput('photoUrl', 'https://evil.example/api/avatar-images/x');
    fixture.detectChanges();
    open();
    send('other');
    fixture.detectChanges();

    expect(text('[data-report-error]')).toBe('Your report was not sent. Try again.');
  });
});
