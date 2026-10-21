import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  EFFECT_AUDIO,
  EffectAudio,
  SOUND_EFFECT_FADE_MS,
  SOUND_EFFECT_LEAST_MS,
  SOUND_EFFECT_URLS,
  SOUND_EFFECT_VOLUME,
  SoundEffects,
} from './sound-effects';
import { ThemeMusic } from './theme-music';

describe('SoundEffects', () => {
  const muted = signal(false);
  let created: (EffectAudio & { url: string; plays: number; paused: boolean })[];

  function setup(refuse = false): SoundEffects {
    TestBed.configureTestingModule({
      providers: [
        { provide: ThemeMusic, useValue: { muted } },
        {
          provide: EFFECT_AUDIO,
          useValue: (url: string) => {
            const audio = {
              url,
              plays: 0,
              volume: 1,
              currentTime: 0,
              play() {
                audio.plays++;
                return refuse ? Promise.reject(new DOMException('No gesture yet', 'NotAllowedError')) : Promise.resolve();
              },
              paused: false,
              pause: () => (audio.paused = true),
            };
            created.push(audio);
            return audio;
          },
        },
      ],
    });
    return TestBed.inject(SoundEffects);
  }

  beforeEach(() => {
    created = [];
    muted.set(false);
  });

  it('plays a sound at its volume, from the start each time, with one element', async () => {
    const sounds = setup();

    sounds.play('diceRoll');
    await Promise.resolve();
    created[0].currentTime = 0.8;
    sounds.play('diceRoll');
    await Promise.resolve();

    expect(created).toHaveLength(1);
    expect(created[0].url).toBe(SOUND_EFFECT_URLS.diceRoll);
    expect(created[0].volume).toBe(SOUND_EFFECT_VOLUME);
    expect(created[0].currentTime).toBe(0);
    expect(created[0].plays).toBe(2);
  });

  it('stays silent while the header’s toggle has muted sound', () => {
    const sounds = setup();
    muted.set(true);

    sounds.play('diceRoll');

    expect(created).toHaveLength(0);
  });

  it('ignores a browser refusing to play before the first interaction', async () => {
    const sounds = setup(true);

    sounds.play('diceRoll');
    await Promise.resolve();
    await Promise.resolve();

    expect(created[0].plays).toBe(1);
  });

  it('fades a sound out and stops it when a car stops, and plays it at full volume again', async () => {
    vi.useFakeTimers();
    try {
      const sounds = setup();
      sounds.play('carForward');
      const car = created[0];

      sounds.stop('carForward');
      vi.advanceTimersByTime(SOUND_EFFECT_FADE_MS / 2);
      expect(car.volume).toBeGreaterThan(0);
      expect(car.volume).toBeLessThan(SOUND_EFFECT_VOLUME);
      expect(car.paused).toBe(false);
      vi.advanceTimersByTime(SOUND_EFFECT_FADE_MS / 2);
      expect(car.volume).toBe(0);
      expect(car.paused).toBe(true);

      sounds.play('carForward');
      expect(car.volume).toBe(SOUND_EFFECT_VOLUME);
      sounds.stop('carForward');
      sounds.play('carForward');
      vi.advanceTimersByTime(SOUND_EFFECT_FADE_MS);
      // Played again mid-fade: the fade is dropped.
      expect(car.volume).toBe(SOUND_EFFECT_VOLUME);
    } finally {
      vi.useRealTimers();
    }
  });

  it('lets the back-up beeper play its two beeps before a stop fades it out', async () => {
    vi.useFakeTimers();
    try {
      const sounds = setup();
      const least = SOUND_EFFECT_LEAST_MS.carReverse ?? 0;
      expect(least).toBeGreaterThan(1080);
      sounds.play('carReverse');
      const beeper = created[0];

      vi.advanceTimersByTime(400);
      sounds.stop('carReverse');
      vi.advanceTimersByTime(least - 400 - 1);
      expect(beeper.volume).toBe(SOUND_EFFECT_VOLUME);
      expect(beeper.paused).toBe(false);

      vi.advanceTimersByTime(1 + SOUND_EFFECT_FADE_MS);
      expect(beeper.paused).toBe(true);

      // Played again before a pending stop: the stop is dropped, and a later one waits for the new start.
      sounds.play('carReverse');
      sounds.stop('carReverse');
      sounds.play('carReverse');
      vi.advanceTimersByTime(least + SOUND_EFFECT_FADE_MS);
      expect(beeper.volume).toBe(SOUND_EFFECT_VOLUME);
    } finally {
      vi.useRealTimers();
    }
  });
});
