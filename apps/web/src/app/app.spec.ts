import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { appRoutes } from './app.routes';

describe('App shell', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(appRoutes)],
    }).compileComponents();
  });

  it('renders the brand and main navigation', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('.brand')?.textContent).toContain('Chog Game Arena');
    const links = Array.from(element.querySelectorAll('nav[aria-label="Main"] a'));
    expect(links.map((a) => a.getAttribute('href'))).toEqual(['/', '/games']);
    expect(element.querySelector('main router-outlet')).not.toBeNull();
  });
});
