import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SoundEffects } from '../shell/sound-effects';
import { DIE_TUMBLE_MS, DieRoll, RollingDie } from './rolling-die';

describe('Rolling die', () => {
  let fixture: ComponentFixture<RollingDie>;
  let played: string[];

  const die = () => (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('[data-die] .die');
  const face = () => Number(die()?.dataset['face']);
  const pips = () => die()?.querySelectorAll('circle').length;

  function show(roll: DieRoll | null): void {
    fixture.componentRef.setInput('roll', roll);
    fixture.detectChanges();
  }

  beforeEach(() => {
    vi.useFakeTimers();
    played = [];
    TestBed.configureTestingModule({
      imports: [RollingDie],
      providers: [{ provide: SoundEffects, useValue: { play: (effect: string) => played.push(effect) } }],
    });
    fixture = TestBed.createComponent(RollingDie);
    fixture.detectChanges();
  });

  afterEach(() => vi.useRealTimers());

  it('shows nothing without a roll', () => {
    expect(die()).toBeNull();
  });

  it('tumbles through other faces with the rolling sound, then lands on the number rolled', () => {
    show({ value: 4, id: 1 });

    expect(played).toEqual(['diceRoll']);
    expect(die()?.classList).not.toContain('landed');
    expect(die()?.closest('[data-die]')?.getAttribute('aria-hidden')).toBe('true');
    const faces = [face()];
    for (let t = 0; t < DIE_TUMBLE_MS - 100; t += 90) {
      vi.advanceTimersByTime(90);
      fixture.detectChanges();
      faces.push(face());
    }
    // Every turn shows a new face, always a real one.
    faces.slice(1).forEach((shown, i) => expect(shown).not.toBe(faces[i]));
    expect(faces.every((shown) => shown >= 1 && shown <= 6)).toBe(true);

    vi.advanceTimersByTime(DIE_TUMBLE_MS);
    fixture.detectChanges();

    expect(die()?.classList).toContain('landed');
    expect(face()).toBe(4);
    expect(pips()).toBe(4);
  });

  it('rolls again for a new roll of the same number, and goes when the board says so', () => {
    show({ value: 6, id: 1 });
    vi.advanceTimersByTime(DIE_TUMBLE_MS);
    show({ value: 6, id: 2 });

    expect(played).toEqual(['diceRoll', 'diceRoll']);
    expect(die()?.classList).not.toContain('landed');

    show(null);
    expect(die()).toBeNull();
  });
});
