import { Component, DestroyRef, computed, inject, input, signal } from '@angular/core';

/** The last seconds of a turn, shown as urgent. */
export const URGENT_SECONDS = 5;

/**
 * How much of the current turn is left (DEC-027, DEC-045): whose turn it is, the seconds left and a bar that drains
 * from full to empty over the match's turn length, turning urgent in the last seconds. It counts down to the server's
 * `endsAt`; the server decides when the turn really runs out.
 */
@Component({
  selector: 'app-turn-timer',
  template: `
    <div class="turn-timer" [class.yours]="isYou()" [class.urgent]="urgent()" data-turn-timer>
      <p class="line" role="timer" [attr.aria-label]="label()">
        <span class="who">{{ isYou() ? 'Your turn' : name() + '’s turn' }}</span>
        <span class="left" data-turn-left>{{ left() > 0 ? left() + 's left' : 'Time’s up' }}</span>
      </p>
      <div class="track" aria-hidden="true">
        <div class="bar" [style.width.%]="percent()" data-turn-bar></div>
      </div>
    </div>
  `,
  styleUrl: './turn-timer.scss',
})
export class TurnTimer {
  /** When the turn runs out (ISO 8601). */
  readonly endsAt = input.required<string>();
  /** How long each turn of the match lasts, in seconds. */
  readonly seconds = input.required<number>();
  /** Whose turn it is, by display name. */
  readonly name = input.required<string>();
  readonly isYou = input(false);

  /** The time now, ticking four times a second so the bar drains smoothly. */
  private readonly now = signal(Date.now());

  constructor() {
    const tick = setInterval(() => this.now.set(Date.now()), 250);
    inject(DestroyRef).onDestroy(() => clearInterval(tick));
  }

  private readonly remaining = computed(() => {
    const end = Date.parse(this.endsAt());
    return Number.isNaN(end) ? 0 : Math.max(0, end - this.now());
  });

  /** Whole seconds left, rounded up, so "1s left" shows until the turn is over. */
  protected readonly left = computed(() => Math.ceil(this.remaining() / 1000));

  protected readonly percent = computed(() => {
    const total = this.seconds() * 1000;
    return total > 0 ? Math.min(100, (this.remaining() / total) * 100) : 0;
  });

  protected readonly urgent = computed(() => this.left() <= URGENT_SECONDS);

  protected readonly label = computed(() => {
    const who = this.isYou() ? 'Your turn' : `${this.name()}’s turn`;
    return this.left() > 0 ? `${who}: ${this.left()} seconds left` : `${who}: time’s up`;
  });
}
