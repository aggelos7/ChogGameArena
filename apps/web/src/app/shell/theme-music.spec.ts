import { TestBed } from '@angular/core/testing';
import { MusicTrack } from './music-track';
import {
  GAME_MUSIC_URLS,
  MUSIC_CROSSFADE_MS,
  MUSIC_MUTE_FADE_MS,
  MUSIC_TRACK,
  THEME_MUSIC_URL,
  THEME_MUSIC_VOLUME,
  ThemeMusic,
} from './theme-music';

/**
 * Stands in for a track: records play/pause and fades, refuses play while `blocked` like a browser without a gesture,
 * and leaves each fade running until the spec calls `finishFade()`. A replaced fade never resolves, as in the real one.
 */
class FakeTrack implements MusicTrack {
  paused = true;
  plays = 0;
  volume = 1;
  fades: [number, number][] = [];
  private pending: (() => void) | null = null;

  constructor(
    readonly url: string,
    private readonly blocked: () => boolean,
  ) {}

  play(): Promise<void> {
    this.plays++;
    if (this.blocked()) {
      return Promise.reject(new DOMException('No gesture yet', 'NotAllowedError'));
    }
    this.paused = false;
    return Promise.resolve();
  }

  pause(): void {
    this.paused = true;
  }

  setVolume(volume: number): void {
    this.volume = volume;
    this.pending = null;
  }

  fade(volume: number, ms: number): Promise<void> {
    this.fades.push([volume, ms]);
    return new Promise((resolve) => {
      this.pending = () => {
        this.volume = volume;
        resolve();
      };
    });
  }

  lastFade(): [number, number] | undefined {
    return this.fades[this.fades.length - 1];
  }

  /** The fade running now reaches its loudness. */
  async finishFade(): Promise<void> {
    const pending = this.pending;
    this.pending = null;
    pending?.();
    await flush();
  }
}

async function flush() {
  for (let i = 0; i < 3; i++) {
    await Promise.resolve();
  }
}

describe('ThemeMusic', () => {
  let created: FakeTrack[];
  let blocked: boolean;

  function setup(): ThemeMusic {
    TestBed.configureTestingModule({
      providers: [
        { provide: MUSIC_TRACK, useValue: (url: string) => created[created.push(new FakeTrack(url, () => blocked)) - 1] },
        { provide: GAME_MUSIC_URLS, useValue: { 'chog-race': 'audio/race.mp3' } },
      ],
    });
    return TestBed.inject(ThemeMusic);
  }

  async function gesture(type: 'pointerdown' | 'keydown' = 'pointerdown') {
    document.body.dispatchEvent(new Event(type, { bubbles: true }));
    await flush();
  }

  beforeEach(() => {
    created = [];
    blocked = false;
    localStorage.clear();
  });

  afterEach(() => localStorage.clear());

  it('creates nothing, so nothing downloads, before the first interaction', () => {
    const music = setup();

    expect(created).toHaveLength(0);
    expect(music.muted()).toBe(false);
    expect(music.playing()).toBe(false);
  });

  it('fades the theme in on the first tap, once for the whole app', async () => {
    const music = setup();

    await gesture();
    await gesture('keydown');

    expect(created).toHaveLength(1);
    const theme = created[0];
    expect(theme.url).toBe(THEME_MUSIC_URL);
    expect(theme.paused).toBe(false);
    expect(theme.plays).toBe(1);
    expect(theme.volume).toBe(0);
    expect(theme.fades).toEqual([[THEME_MUSIC_VOLUME, MUSIC_CROSSFADE_MS]]);
    expect(music.playing()).toBe(true);

    await theme.finishFade();
    expect(theme.volume).toBe(THEME_MUSIC_VOLUME);
  });

  it('starts on a key press too', async () => {
    setup();

    await gesture('keydown');

    expect(created[0].paused).toBe(false);
  });

  it('tries again on the next interaction when the browser refuses to play', async () => {
    const music = setup();
    blocked = true;

    await gesture();
    expect(music.playing()).toBe(false);

    blocked = false;
    await gesture();

    expect(created).toHaveLength(1);
    expect(created[0].plays).toBe(2);
    expect(music.playing()).toBe(true);
  });

  it('fades out on mute and pauses only then, remembers it, and a remembered mute keeps the page silent', async () => {
    const music = setup();
    await gesture();
    const theme = created[0];
    await theme.finishFade();

    music.toggle();

    expect(music.muted()).toBe(true);
    expect(music.playing()).toBe(false);
    expect(theme.lastFade()).toEqual([0, MUSIC_MUTE_FADE_MS]);
    expect(theme.paused).toBe(false);
    await theme.finishFade();
    expect(theme.paused).toBe(true);
    expect(localStorage.getItem('chogKingdom.musicMuted')).toBe('true');

    await gesture();
    expect(theme.paused).toBe(true);

    TestBed.resetTestingModule();
    const next = setup();
    await gesture();

    expect(next.muted()).toBe(true);
    expect(created).toHaveLength(1);
  });

  it('fades in quickly when unmuted, and remembers that', async () => {
    localStorage.setItem('chogKingdom.musicMuted', 'true');
    const music = setup();

    music.toggle();
    await flush();

    expect(music.muted()).toBe(false);
    expect(created[0].paused).toBe(false);
    expect(created[0].fades).toEqual([[THEME_MUSIC_VOLUME, MUSIC_MUTE_FADE_MS]]);
    expect(localStorage.getItem('chogKingdom.musicMuted')).toBe('false');
  });

  it('keeps playing when unmuted again before the mute’s fade finished', async () => {
    const music = setup();
    await gesture();
    const theme = created[0];

    music.toggle();
    music.toggle();
    await theme.finishFade();

    expect(theme.paused).toBe(false);
    expect(theme.volume).toBe(THEME_MUSIC_VOLUME);
    expect(theme.plays).toBe(2);
  });

  it('crossfades from the theme to the game’s music during a live match, and back to where the theme was', async () => {
    const music = setup();
    await gesture();
    const theme = created[0];
    await theme.finishFade();

    music.setLiveGame('chog-race');
    await flush();

    const race = created[1];
    expect(race.url).toBe('audio/race.mp3');
    expect(race.paused).toBe(false);
    expect(race.fades).toEqual([[THEME_MUSIC_VOLUME, MUSIC_CROSSFADE_MS]]);
    // Both play while one fades out and the other in.
    expect(theme.lastFade()).toEqual([0, MUSIC_CROSSFADE_MS]);
    expect(theme.paused).toBe(false);
    await theme.finishFade();
    expect(theme.paused).toBe(true);

    music.setLiveGame(null);
    await flush();

    expect(theme.paused).toBe(false);
    expect(theme.lastFade()).toEqual([THEME_MUSIC_VOLUME, MUSIC_CROSSFADE_MS]);
    expect(race.lastFade()).toEqual([0, MUSIC_CROSSFADE_MS]);
    await race.finishFade();
    expect(race.paused).toBe(true);
    expect(created).toHaveLength(2);
  });

  it('fades the theme back in from where it was when a match ends during the crossfade', async () => {
    const music = setup();
    await gesture();
    const theme = created[0];

    music.setLiveGame('chog-race');
    music.setLiveGame(null);
    await theme.finishFade();

    expect(theme.paused).toBe(false);
    expect(theme.volume).toBe(THEME_MUSIC_VOLUME);
  });

  it('fades out to silence in a live match of a game without music', async () => {
    const music = setup();
    await gesture();
    const theme = created[0];

    music.setLiveGame('chog-life');
    await theme.finishFade();
    await gesture();

    expect(created).toHaveLength(1);
    expect(theme.paused).toBe(true);
    expect(music.playing()).toBe(false);
  });

  it('waits for the first interaction even when a live match comes first', async () => {
    const music = setup();

    music.setLiveGame('chog-race');
    expect(created).toHaveLength(0);

    await gesture();
    expect(created.map((track) => track.url)).toEqual(['audio/race.mp3']);
  });

  it('mutes the game’s music too, and a match starting while muted stays silent', async () => {
    const music = setup();
    await gesture();
    music.toggle();

    music.setLiveGame('chog-race');
    expect(created).toHaveLength(1);

    music.toggle();
    await flush();
    const race = created[1];
    expect(race.paused).toBe(false);

    music.toggle();
    await race.finishFade();
    expect(race.paused).toBe(true);
  });
});
