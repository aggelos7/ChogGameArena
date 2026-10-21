import { Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { SoundEffects } from '../shell/sound-effects';

/** How long the die tumbles before it lands on the number rolled (DEC-076). */
export const DIE_TUMBLE_MS = 700;
/** How long the die shows from the roll until the pawn starts to hop: the tumble, then the number for a moment. */
export const DIE_MS = 1200;
/** How often the tumbling die shows another face. */
const FACE_MS = 90;

/** A roll to show: the server's number, and an id that's new for each roll, so the same number twice tumbles twice. */
export interface DieRoll {
  value: number;
  id: number;
}

/** Where each face's pips sit on a 3 × 3 grid, in a 100-unit square. */
const PIPS: Record<number, [number, number][]> = {
  1: [[50, 50]],
  2: [[28, 28], [72, 72]],
  3: [[28, 28], [50, 50], [72, 72]],
  4: [[28, 28], [72, 28], [28, 72], [72, 72]],
  5: [[28, 28], [72, 28], [50, 50], [28, 72], [72, 72]],
  6: [[28, 28], [72, 28], [28, 50], [72, 50], [28, 72], [72, 72]],
};

/**
 * The die over a board while a roll is shown (STORY-089, DEC-076), for Chog Life and Chog Race alike: it tumbles through
 * random faces with the rolling sound, then lands on the server's number. Purely a picture of what the server already
 * decided; the board says when to show it, and the last-move line tells screen readers the number.
 */
@Component({
  selector: 'app-rolling-die',
  template: `
    @if (roll()) {
      <div class="die-layer" aria-hidden="true" data-die>
        <div class="die" [class.landed]="landed()" [attr.data-face]="face()">
          <svg viewBox="0 0 100 100">
            <rect x="4" y="4" width="92" height="92" rx="18" />
            @for (pip of pips(); track $index) {
              <circle [attr.cx]="pip[0]" [attr.cy]="pip[1]" r="9" />
            }
          </svg>
        </div>
      </div>
    }
  `,
  styleUrl: './rolling-die.scss',
})
export class RollingDie {
  private readonly sounds = inject(SoundEffects);

  readonly roll = input<DieRoll | null>(null);

  protected readonly face = signal(1);
  protected readonly landed = signal(false);
  protected readonly pips = () => PIPS[this.face()] ?? [];

  private tumble: ReturnType<typeof setInterval> | undefined;
  private land: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    effect(() => {
      const roll = this.roll();
      untracked(() => this.show(roll));
    });
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  private show(roll: DieRoll | null): void {
    this.stop();
    if (!roll) {
      return;
    }
    this.landed.set(false);
    this.face.set(this.otherFace(roll.value));
    this.sounds.play('diceRoll');
    this.tumble = setInterval(() => this.face.set(this.otherFace(this.face())), FACE_MS);
    this.land = setTimeout(() => {
      clearInterval(this.tumble);
      this.face.set(roll.value);
      this.landed.set(true);
    }, DIE_TUMBLE_MS);
  }

  /** A random face other than `face`, so every turn of the tumble looks like one. Only for show, never a game draw. */
  private otherFace(face: number): number {
    const next = 1 + Math.floor(Math.random() * 5);
    return next >= face ? next + 1 : next;
  }

  private stop(): void {
    clearInterval(this.tumble);
    clearTimeout(this.land);
  }
}
