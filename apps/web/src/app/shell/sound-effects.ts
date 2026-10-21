import { DestroyRef, Injectable, InjectionToken, inject } from '@angular/core';
import { ThemeMusic } from './theme-music';

/** The game sounds, served from `public/audio` (DEC-076). */
export const SOUND_EFFECT_URLS = {
  diceRoll: 'audio/dice-roll.mp3',
  carForward: 'audio/car-forward.mp3',
  carReverse: 'audio/car-reverse.mp3',
} as const;

export type SoundEffect = keyof typeof SOUND_EFFECT_URLS;

/** A little louder than the music, so a roll is heard over it. */
export const SOUND_EFFECT_VOLUME = 0.6;
/** How long a sound stopped early takes to fade out. */
export const SOUND_EFFECT_FADE_MS = 200;
/**
 * How long a sound plays at least before a stop fades it out: the back-up beeper's two beeps (it beeps from 0.05 s to
 * 0.44 s and 0.71 s to 1.08 s, the third at 1.36 s).
 */
export const SOUND_EFFECT_LEAST_MS: Partial<Record<SoundEffect, number>> = { carReverse: 1100 };
const FADE_STEPS = 5;

/** The parts of an audio element a sound uses; a token so tests can fake it. */
export interface EffectAudio {
  volume: number;
  currentTime: number;
  play(): Promise<void>;
  pause(): void;
}

export const EFFECT_AUDIO = new InjectionToken<(url: string) => EffectAudio>('EFFECT_AUDIO', {
  providedIn: 'root',
  factory: () => (url) => new Audio(url),
});

/**
 * Short game sounds (STORY-089, DEC-076), such as the die rolling. The header's music toggle mutes them too. Each sound
 * has one element, created when it first plays; playing it again starts it over. Before the visitor's first interaction
 * the browser refuses to play, which is ignored.
 */
@Injectable({ providedIn: 'root' })
export class SoundEffects {
  private readonly music = inject(ThemeMusic);
  private readonly createAudio = inject(EFFECT_AUDIO);
  private readonly sounds = new Map<SoundEffect, EffectAudio>();
  private readonly fades = new Map<SoundEffect, ReturnType<typeof setInterval>>();
  private readonly started = new Map<SoundEffect, number>();
  private readonly pendingStops = new Map<SoundEffect, ReturnType<typeof setTimeout>>();

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.fades.forEach((fade) => clearInterval(fade));
      this.pendingStops.forEach((stop) => clearTimeout(stop));
      this.sounds.forEach((sound) => sound.pause());
    });
  }

  play(effect: SoundEffect): void {
    if (this.music.muted()) {
      return;
    }
    let sound = this.sounds.get(effect);
    if (!sound) {
      sound = this.createAudio(SOUND_EFFECT_URLS[effect]);
      sound.volume = SOUND_EFFECT_VOLUME;
      this.sounds.set(effect, sound);
    }
    // Played again while fading out, or about to: it starts over at full volume.
    clearInterval(this.fades.get(effect));
    this.fades.delete(effect);
    clearTimeout(this.pendingStops.get(effect));
    this.pendingStops.delete(effect);
    this.started.set(effect, Date.now());
    sound.volume = SOUND_EFFECT_VOLUME;
    sound.currentTime = 0;
    // Older browsers return nothing from play() and may throw; a refusal is ignored either way.
    Promise.resolve()
      .then(() => sound.play())
      .catch(() => undefined);
  }

  /**
   * Fades a playing sound out and stops it, as when a car stops before its sound ends; a sound with a least length is
   * let play until then first. Nothing if it isn't playing.
   */
  stop(effect: SoundEffect): void {
    const sound = this.sounds.get(effect);
    if (!sound || this.fades.has(effect) || this.pendingStops.has(effect)) {
      return;
    }
    const wait = (SOUND_EFFECT_LEAST_MS[effect] ?? 0) - (Date.now() - (this.started.get(effect) ?? 0));
    if (wait > 0) {
      this.pendingStops.set(
        effect,
        setTimeout(() => {
          this.pendingStops.delete(effect);
          this.stop(effect);
        }, wait),
      );
      return;
    }
    let step = 0;
    const fade = setInterval(() => {
      step++;
      sound.volume = SOUND_EFFECT_VOLUME * Math.max(0, 1 - step / FADE_STEPS);
      if (step >= FADE_STEPS) {
        clearInterval(fade);
        this.fades.delete(effect);
        sound.pause();
      }
    }, SOUND_EFFECT_FADE_MS / FADE_STEPS);
    this.fades.set(effect, fade);
  }
}
