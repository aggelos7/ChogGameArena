import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, InjectionToken, inject, signal } from '@angular/core';
import { ElementMusicTrack, MusicTrack } from './music-track';

/** The theme track (DEC-076), served from `public/audio`. */
export const THEME_MUSIC_URL = 'audio/chog-kingdom-theme.mp3';
/** Quiet enough to sit under the games. */
export const THEME_MUSIC_VOLUME = 0.35;
/** One track fades out while the next fades in. */
export const MUSIC_CROSSFADE_MS = 1500;
/** Muting and unmuting fade quickly, so the toggle still feels immediate. */
export const MUSIC_MUTE_FADE_MS = 400;
const MUTED_KEY = 'chogKingdom.musicMuted';

/** Creates a looping track; only called when it first plays, so nothing downloads before it. A token so tests can fake it. */
export const MUSIC_TRACK = new InjectionToken<(url: string) => MusicTrack>('MUSIC_TRACK', {
  providedIn: 'root',
  factory: () => (url) => new ElementMusicTrack(url),
});

/** Each game's own music during a live match, by game type; a game without one is silent (DEC-076). */
export const GAME_MUSIC_URLS = new InjectionToken<Readonly<Record<string, string>>>('GAME_MUSIC_URLS', {
  providedIn: 'root',
  factory: () => ({ 'chog-life': 'audio/chog-life.mp3', 'chog-race': 'audio/chog-race.mp3', 'chog-royale': 'audio/chog-royale.mp3' }),
});

/**
 * The site's music (STORY-089, DEC-076). The theme is on by default: browsers only allow sound after the visitor
 * interacts with the page, so it starts on their first click, tap or key press and then loops across pages. During a
 * live match the theme pauses and the game's own track plays instead; the theme resumes where it was afterwards. The
 * old track fades out while the new one fades in, and muting fades too. One track object per piece for the whole app,
 * so navigating never restarts one. Muting covers all music and is remembered on the device.
 */
@Injectable({ providedIn: 'root' })
export class ThemeMusic {
  private readonly document = inject(DOCUMENT);
  private readonly createTrack = inject(MUSIC_TRACK);
  private readonly gameTracks = inject(GAME_MUSIC_URLS);
  private readonly tracks = new Map<string, MusicTrack>();
  /** The track playing or about to; null while nothing should. One fading out isn't current. */
  private current: MusicTrack | null = null;
  /** The visitor interacted, so the browser allows sound and a track can start without waiting for another gesture. */
  private allowed = false;

  readonly muted = signal(readMuted());
  readonly playing = signal(false);
  /** The game whose match the player is in live, or null. */
  readonly liveGame = signal<string | null>(null);

  private readonly stopListening: () => void;

  constructor() {
    const events = ['pointerdown', 'keydown'] as const;
    const startOnGesture = () => {
      this.allowed = true;
      if (!this.muted() && !this.playing()) {
        this.play(MUSIC_CROSSFADE_MS);
      }
    };
    // Capture, so the gesture counts even when a page stops it from bubbling. Listeners stay until playback started,
    // because a browser may still refuse the first attempt.
    for (const event of events) {
      this.document.addEventListener(event, startOnGesture, { capture: true });
    }
    this.stopListening = () => {
      for (const event of events) {
        this.document.removeEventListener(event, startOnGesture, { capture: true });
      }
    };
    inject(DestroyRef).onDestroy(() => {
      this.stopListening();
      this.tracks.forEach((track) => track.pause());
    });
  }

  /** The header's toggle: mutes and pauses, or unmutes and plays (the click itself allows sound). */
  toggle(): void {
    const muted = !this.muted();
    this.muted.set(muted);
    try {
      globalThis.localStorage?.setItem(MUTED_KEY, String(muted));
    } catch {
      // Storage can be blocked; the choice then lasts for this visit only.
    }
    if (muted) {
      this.stop(MUSIC_MUTE_FADE_MS);
    } else {
      this.allowed = true;
      this.play(MUSIC_MUTE_FADE_MS);
    }
  }

  /** The room page reports the game of a live match the player is in, and null once it's over or they left. */
  setLiveGame(gameType: string | null): void {
    if (this.liveGame() === gameType) {
      return;
    }
    this.liveGame.set(gameType);
    this.stop(MUSIC_CROSSFADE_MS);
    if (this.allowed && !this.muted()) {
      this.play(MUSIC_CROSSFADE_MS);
    }
  }

  private play(fadeMs: number): void {
    const game = this.liveGame();
    const url = game === null ? THEME_MUSIC_URL : this.gameTracks[game];
    if (!url) {
      return;
    }
    let track = this.tracks.get(url);
    if (!track) {
      track = this.createTrack(url);
      this.tracks.set(url, track);
    }
    const started = track;
    // A track still fading out fades back in from where it is; a paused one starts silent.
    if (started.paused) {
      started.setVolume(0);
    }
    this.current = started;
    this.playing.set(true);
    started.play().then(
      () => this.stopListening(),
      // Refused (no gesture yet, or the file failed): try again on the next one, unless another track took over.
      () => {
        if (this.current === started) {
          this.playing.set(false);
        }
      },
    );
    void started.fade(THEME_MUSIC_VOLUME, fadeMs);
  }

  /** Fades the current track out and pauses it there, so it resumes where it was; unless it's playing again by then. */
  private stop(fadeMs: number): void {
    const stopping = this.current;
    this.current = null;
    this.playing.set(false);
    stopping?.fade(0, fadeMs).then(() => {
      if (this.current !== stopping) {
        stopping.pause();
      }
    });
  }
}

function readMuted(): boolean {
  try {
    return globalThis.localStorage?.getItem(MUTED_KEY) === 'true';
  } catch {
    return false;
  }
}
