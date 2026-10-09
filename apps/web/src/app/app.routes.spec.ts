import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { appRoutes } from './app.routes';

describe('app routes', () => {
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideRouter(appRoutes)] });
    harness = await RouterTestingHarness.create();
  });

  const heading = () => (harness.routeNativeElement as HTMLElement).querySelector('h1')?.textContent;

  it('renders the home page at /', async () => {
    await harness.navigateByUrl('/');

    expect(heading()).toBe('Chog Game Arena');
    expect(TestBed.inject(Title).getTitle()).toBe('Chog Game Arena');
  });

  it('renders the games page at /games', async () => {
    await harness.navigateByUrl('/games');

    expect(heading()).toBe('Games');
    expect(TestBed.inject(Title).getTitle()).toBe('Games · Chog Game Arena');
  });

  it('redirects unknown URLs to the home page', async () => {
    await harness.navigateByUrl('/does-not-exist');

    expect(TestBed.inject(Router).url).toBe('/');
    expect(heading()).toBe('Chog Game Arena');
  });
});
