import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TurnTimer } from './turn-timer';

describe('Turn timer', () => {
  let fixture: ComponentFixture<TurnTimer>;
  let page: HTMLElement;
  const start = Date.parse('2026-10-14T12:00:00Z');

  function render(secondsLeft: number, isYou: boolean, name = 'Bob'): void {
    fixture.componentRef.setInput('endsAt', new Date(start + secondsLeft * 1000).toISOString());
    fixture.componentRef.setInput('seconds', 25);
    fixture.componentRef.setInput('name', name);
    fixture.componentRef.setInput('isYou', isYou);
    fixture.detectChanges();
  }
  const text = (selector: string) => page.querySelector(selector)?.textContent?.trim();
  const width = () => page.querySelector<HTMLElement>('[data-turn-bar]')?.style.width;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(start);
    TestBed.configureTestingModule({ imports: [TurnTimer] });
    fixture = TestBed.createComponent(TurnTimer);
    page = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => vi.useRealTimers());

  it('shows your turn with the seconds left and a full bar at the start', () => {
    render(25, true);

    expect(text('.who')).toBe('Your turn');
    expect(text('[data-turn-left]')).toBe('25s left');
    expect(width()).toBe('100%');
    expect(page.querySelector('[role="timer"]')?.getAttribute('aria-label')).toBe('Your turn: 25 seconds left');
    expect(page.querySelector('.turn-timer.yours')).not.toBeNull();
    expect(page.querySelector('.urgent')).toBeNull();
  });

  it('drains as time passes and turns urgent in the last five seconds', () => {
    render(25, false);
    expect(text('.who')).toBe('Bob’s turn');

    vi.advanceTimersByTime(15_000);
    fixture.detectChanges();
    expect(text('[data-turn-left]')).toBe('10s left');
    expect(width()).toBe('40%');
    expect(page.querySelector('.urgent')).toBeNull();

    vi.advanceTimersByTime(5_500);
    fixture.detectChanges();
    expect(text('[data-turn-left]')).toBe('5s left');
    expect(page.querySelector('.turn-timer.urgent')).not.toBeNull();

    vi.advanceTimersByTime(10_000);
    fixture.detectChanges();
    expect(text('[data-turn-left]')).toBe('Time’s up');
    expect(width()).toBe('0%');
  });
});
