import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TERMS_VERSION } from '../../legal/legal-api';
import { PrivacyPage } from './privacy';
import { TermsPage } from './terms';

describe('Terms and privacy pages (DEC-070)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function render(page: Type<unknown>, contactEmail: string | null) {
    const fixture = TestBed.createComponent(page);
    fixture.detectChanges();
    for (const request of http.match('/api/legal')) {
      request.flush({ termsVersion: TERMS_VERSION, contactEmail });
    }

    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the terms with their version, the photo rules and the contact address', () => {
    const page = render(TermsPage, 'privacy@example.com');

    expect(page.querySelector('[data-terms-version]')?.textContent).toContain(TERMS_VERSION);
    expect(page.textContent).toContain('Your photo');
    expect(page.textContent).toContain('MON in the games is fictional');
    expect(page.querySelector('[data-contact]')?.getAttribute('href')).toBe('mailto:privacy@example.com');
    expect(page.querySelector('a[href="/privacy"]')).not.toBeNull();
  });

  it('shows what is stored and how to ask for deletion', () => {
    const page = render(PrivacyPage, 'privacy@example.com');

    for (const part of ['Your session', 'Your photo', 'Your wallet address', 'Reports', 'about four weeks']) {
      expect(page.textContent).toContain(part);
    }

    expect(page.querySelector('[data-contact]')?.textContent).toBe('privacy@example.com');
  });

  it('falls back to a plain line when the deployment set no contact address', () => {
    const page = render(PrivacyPage, null);

    expect(page.querySelector('[data-contact]')).toBeNull();
    expect(page.querySelector('[data-contact-missing]')).not.toBeNull();
  });
});
