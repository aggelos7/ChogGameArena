/** One looping piece of music whose loudness can be faded (DEC-076). */
export interface MusicTrack {
  readonly paused: boolean;
  /** Starts or resumes where it paused, at its current loudness. */
  play(): Promise<void>;
  pause(): void;
  /** Sets the loudness (0 to 1) at once. */
  setVolume(volume: number): void;
  /** Moves the loudness to `volume` over `ms` and resolves when it's reached; a fade replaced by another never resolves. */
  fade(volume: number, ms: number): Promise<void>;
}

const STEP_MS = 50;
let sharedContext: AudioContext | null | undefined;

/**
 * Creates the Web Audio context the tracks share, on the first track, which is created on a visitor's gesture so the
 * browser lets it run. Null where there's none.
 */
function audioContext(): AudioContext | null {
  if (sharedContext === undefined) {
    try {
      sharedContext = typeof AudioContext === 'function' ? new AudioContext() : null;
    } catch {
      sharedContext = null;
    }
  }
  return sharedContext;
}

/**
 * A looping `<audio>` element routed through a Web Audio gain node. iOS ignores an audio element's `volume`, so the
 * gain sets the loudness and fades it smoothly everywhere. Without Web Audio the element's `volume` is stepped instead.
 */
export class ElementMusicTrack implements MusicTrack {
  private readonly audio: HTMLAudioElement;
  private readonly gain: GainNode | null = null;
  /**
   * The gain's latest change, in the context's time. The loudness is worked out from it rather than read back from the
   * gain, whose `value` only shows a change once the audio thread applied it: a fade right after `setVolume(0)` would
   * otherwise start from the old loudness, and the track would come in loud.
   */
  private ramp = { from: 1, to: 1, start: 0, end: 0 };
  private fadeEnd?: ReturnType<typeof setTimeout>;
  private fadeSteps?: ReturnType<typeof setInterval>;

  constructor(
    url: string,
    private readonly context: AudioContext | null = audioContext(),
  ) {
    this.audio = new Audio(url);
    this.audio.loop = true;
    if (this.context) {
      try {
        this.gain = this.context.createGain();
        this.context.createMediaElementSource(this.audio).connect(this.gain).connect(this.context.destination);
      } catch {
        this.gain = null;
      }
    }
  }

  get paused(): boolean {
    return this.audio.paused;
  }

  play(): Promise<void> {
    // A context created before the browser allowed sound starts suspended.
    if (this.context?.state === 'suspended') {
      void this.context.resume().catch(() => undefined);
    }
    return this.audio.play();
  }

  pause(): void {
    this.audio.pause();
  }

  setVolume(volume: number): void {
    this.stopFade();
    if (this.gain && this.context) {
      const now = this.context.currentTime;
      this.ramp = { from: volume, to: volume, start: now, end: now };
      this.gain.gain.cancelScheduledValues(0);
      this.gain.gain.setValueAtTime(volume, now);
    } else {
      this.audio.volume = volume;
    }
  }

  fade(volume: number, ms: number): Promise<void> {
    this.stopFade();
    return new Promise((resolve) => {
      if (this.gain && this.context) {
        const gain = this.gain.gain;
        const now = this.context.currentTime;
        const from = this.level(now);
        this.ramp = { from, to: volume, start: now, end: now + ms / 1000 };
        gain.cancelScheduledValues(0);
        gain.setValueAtTime(from, now);
        gain.linearRampToValueAtTime(volume, this.ramp.end);
      } else {
        const from = this.audio.volume;
        const started = Date.now();
        this.fadeSteps = setInterval(() => {
          const done = Math.min(1, (Date.now() - started) / ms);
          this.audio.volume = from + (volume - from) * done;
        }, STEP_MS);
      }
      this.fadeEnd = setTimeout(() => {
        this.stopFade();
        if (!this.gain) {
          this.audio.volume = volume;
        }
        resolve();
      }, ms);
    });
  }

  private stopFade(): void {
    clearTimeout(this.fadeEnd);
    clearInterval(this.fadeSteps);
  }

  /** The gain's loudness at `time`, along its latest ramp. */
  private level(time: number): number {
    const { from, to, start, end } = this.ramp;
    return time >= end ? to : from + ((to - from) * (time - start)) / (end - start);
  }
}
