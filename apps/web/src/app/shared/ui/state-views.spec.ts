import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { EmptyState, ErrorState, LoadingState } from './state-views';

@Component({
  imports: [LoadingState, ErrorState, EmptyState],
  template: `
    <app-loading-state message="Loading things…" />
    <app-error-state message="Things failed." (retry)="retries.set(retries() + 1)" />
    <app-empty-state message="Nothing here."><a href="/next">Next step</a></app-empty-state>
  `,
})
class Host {
  readonly retries = signal(0);
}

describe('State views', () => {
  async function render() {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    return { host: fixture.componentInstance, element: fixture.nativeElement as HTMLElement };
  }

  it('announces loading as a status message', async () => {
    const { element } = await render();

    expect(element.querySelector('app-loading-state [role="status"]')?.textContent?.trim()).toBe('Loading things…');
  });

  it('announces errors as an alert with a native retry button', async () => {
    const { host, element } = await render();
    const alert = element.querySelector('app-error-state [role="alert"]') as HTMLElement;
    const button = alert.querySelector('button') as HTMLButtonElement;

    expect(alert.textContent).toContain('Things failed.');
    expect(button.type).toBe('button');
    expect(button.textContent?.trim()).toBe('Try again');

    button.click();
    expect(host.retries()).toBe(1);
  });

  it('shows the empty message and projected next step', async () => {
    const { element } = await render();
    const empty = element.querySelector('app-empty-state') as HTMLElement;

    expect(empty.textContent).toContain('Nothing here.');
    expect(empty.querySelector('a')?.getAttribute('href')).toBe('/next');
  });
});
