import { ElementMusicTrack } from './music-track';

// The test DOM has no Web Audio, so these cover the fallback that steps the element's own volume.
describe('ElementMusicTrack without Web Audio', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const volume = (track: ElementMusicTrack) => (track as unknown as { audio: HTMLAudioElement }).audio.volume;

  it('loops the track at the given address', () => {
    const track = new ElementMusicTrack('audio/chog-life.mp3');
    const audio = (track as unknown as { audio: HTMLAudioElement }).audio;

    expect(audio.loop).toBe(true);
    expect(audio.src).toContain('audio/chog-life.mp3');
    expect(track.paused).toBe(true);
  });

  it('fades the volume step by step and resolves when it gets there', async () => {
    const track = new ElementMusicTrack('audio/chog-life.mp3');
    track.setVolume(0);
    let done = false;
    void track.fade(0.4, 1000).then(() => (done = true));

    await vi.advanceTimersByTimeAsync(500);
    expect(volume(track)).toBeCloseTo(0.2, 1);
    expect(done).toBe(false);

    await vi.advanceTimersByTimeAsync(500);
    expect(volume(track)).toBeCloseTo(0.4, 5);
    expect(done).toBe(true);
  });

  it('lets a new fade take over from where the last one got to, which then never resolves', async () => {
    const track = new ElementMusicTrack('audio/chog-life.mp3');
    track.setVolume(0.4);
    let firstDone = false;
    void track.fade(0, 1000).then(() => (firstDone = true));
    await vi.advanceTimersByTimeAsync(500);

    void track.fade(0.4, 400);
    await vi.advanceTimersByTimeAsync(2000);

    expect(volume(track)).toBeCloseTo(0.4, 5);
    expect(firstDone).toBe(false);
  });
});

/** Records what the track schedules on its gain, with a clock the spec moves. */
class FakeGainContext {
  currentTime = 0;
  state: AudioContextState = 'running';
  readonly destination = {};
  readonly events: string[] = [];
  readonly gain = {
    value: 1,
    cancelScheduledValues: (time: number) => this.events.push(`cancel@${time}`),
    setValueAtTime: (value: number, time: number) => this.events.push(`set ${+value.toFixed(6)}@${time}`),
    linearRampToValueAtTime: (value: number, time: number) => this.events.push(`ramp ${+value.toFixed(6)}@${+time.toFixed(6)}`),
  };

  createGain() {
    return { gain: this.gain, connect: (node: unknown) => node };
  }

  createMediaElementSource() {
    return { connect: (node: unknown) => node };
  }

  resume() {
    return Promise.resolve();
  }
}

describe('ElementMusicTrack with Web Audio', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const track = (context: FakeGainContext) => new ElementMusicTrack('audio/chog-race.mp3', context as unknown as AudioContext);

  it('fades a new track in from silence, not from the gain’s default the audio thread has yet to change', () => {
    const context = new FakeGainContext();
    const race = track(context);
    context.currentTime = 2;

    race.setVolume(0);
    void race.fade(0.35, 1500);

    // The gain still reads 1 here, as in a browser before the audio thread runs; the ramp must start at 0 regardless.
    expect(context.events).toEqual(['cancel@0', 'set 0@2', 'cancel@0', 'set 0@2', 'ramp 0.35@3.5']);
  });

  it('starts a fade that replaces another from where that one got to', () => {
    const context = new FakeGainContext();
    const theme = track(context);
    theme.setVolume(0.4);
    void theme.fade(0, 1000);
    context.currentTime = 0.25;
    context.events.length = 0;

    void theme.fade(0.4, 400);

    expect(context.events).toEqual(['cancel@0', 'set 0.3@0.25', 'ramp 0.4@0.65']);
  });

  it('resolves a fade when its time is up', async () => {
    const context = new FakeGainContext();
    const theme = track(context);
    let done = false;
    void theme.fade(0, 1500).then(() => (done = true));

    await vi.advanceTimersByTimeAsync(1499);
    expect(done).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(done).toBe(true);
  });
});
