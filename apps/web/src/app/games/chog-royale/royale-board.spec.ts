import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RoomAbility, RoomMember } from '../../rooms/rooms-api';
import { royaleState } from '../../testing';
import { REVEAL_HOLD_MS, RoyaleAbilityUse, RoyaleBoardView, RoyaleSubmission } from './royale-board';
import { RoyaleBoard, toRoyaleBoard } from './royale-board-model';

const withoutMotion = () => vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(prefers-reduced-motion: reduce)' }));

describe('Chog Royale board', () => {
  const start = Date.parse('2026-10-22T12:00:00Z');
  const at = (seconds: number) => new Date(start + seconds * 1000).toISOString();

  function member(displayName: string, seat: number, overrides: Partial<RoomMember> = {}): RoomMember {
    return {
      displayName,
      avatarId: 'classic',
      collector: null,
      avatarImageUrl: null,
      avatar: { id: 'classic', name: 'Classic', color: '#5b2bd6', imageUrl: null },
      isHost: false,
      isYou: false,
      isReady: false,
      character: { kind: 'Standard', id: 'classic', name: 'Classic', color: '#5b2bd6' },
      ability: null,
      hasAbility: true,
      hasLeft: false,
      isTurn: false,
      rank: null,
      seat,
      result: null,
      ...overrides,
    };
  }

  const stopLoss: RoomAbility = {
    id: 'stop_loss',
    name: 'Stop Loss',
    description: 'Use while deciding. This round, other players’ cards take at most 5 MON from you.',
    target: 'None',
    maxUses: 1,
    values: { mon: 5 },
    usesLeft: 1,
  };

  let fixture: ComponentFixture<RoyaleBoardView>;
  let page: HTMLElement;
  let sent: RoyaleSubmission[];
  let used: RoyaleAbilityUse[];

  /** Renders the board: the round opened `opened` seconds ago (negative: opens later) and decides for 15 s. */
  function render(state: Record<string, unknown> = royaleState(), members: RoomMember[] = defaultMembers(), opened = 3, offset = 0): void {
    fixture.componentRef.setInput('board', toRoyaleBoard(state) as RoyaleBoard);
    fixture.componentRef.setInput('members', members);
    fixture.componentRef.setInput('opensAt', at(-opened));
    fixture.componentRef.setInput('endsAt', at(15 - opened));
    fixture.componentRef.setInput('clockOffset', offset);
    fixture.detectChanges();
  }

  const defaultMembers = (you: Partial<RoomMember> = {}, alice: Partial<RoomMember> = {}) => [
    member('Alice', 1, alice),
    member('Bob', 2, { isYou: true, ability: stopLoss, ...you }),
  ];

  const text = (selector: string) => page.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const card = (name: string) => (page.querySelector(`[data-card="${name}"]`) as HTMLButtonElement);
  const button = (selector: string) => (page.querySelector(selector) as HTMLButtonElement);

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(start);
    TestBed.configureTestingModule({ imports: [RoyaleBoardView] });
    fixture = TestBed.createComponent(RoyaleBoardView);
    page = fixture.nativeElement as HTMLElement;
    sent = [];
    used = [];
    fixture.componentInstance.act.subscribe((submission) => sent.push(submission));
    fixture.componentInstance.useAbility.subscribe((use) => used.push(use));
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('shows the round, its countdown, the market event, the players and your three cards', () => {
    render();

    expect(text('[data-round]')).toBe('Round 2 of 5');
    expect(text('[data-timer]')).toBe('12s left to decide');
    expect(page.querySelector('[role="timer"]')?.getAttribute('aria-label')).toBe('Round 2: 12s left to decide');
    expect(text('[data-event-name]')).toBe('Meme Hype');
    expect(text('[data-event]')).toContain('Pumps gain 50% more.');
    expect(
      Array.from(page.querySelectorAll('[data-table] [data-seat]')).map((li) =>
        ['.name', '[data-mon]', '[data-status]'].map((part) => li.querySelector(part)?.textContent?.replace(/\s+/g, ' ').trim()),
      ),
    ).toEqual([
      ['Alice', '115 MON', 'Deciding… · 3 cards'],
      ['Bob You', '85 MON', 'Deciding… · 3 cards'],
    ]);
    expect(Array.from(page.querySelectorAll('[data-list="hand"] [data-card]')).map((c) => c.getAttribute('data-card'))).toEqual([
      'Pump',
      'RugPull',
      'Airdrop',
      'Hodl',
    ]);
    expect(text('[data-pending]')).toBe('You haven’t chosen yet: if time runs out, you HODL.');
  });

  it('shows others only as having chosen, never what', () => {
    render(royaleState(), defaultMembers({}, { hasSubmitted: true, hasLocked: true }));

    expect(page.querySelector('[data-seat="1"] [data-status]')?.getAttribute('data-status')).toBe('locked');
    expect(text('[data-seat="1"] [data-status]')).toBe('✓ Chosen · 3 cards');
  });

  it('turns urgent in the last five seconds and counts on the server’s clock', () => {
    // This device's clock is 8 s behind the server's: 12 s left on the device are 4 s on the server.
    render(royaleState(), defaultMembers(), 3, 8_000);

    expect(text('[data-timer]')).toBe('4s left to decide');
    expect(page.querySelector('[data-round-bar].urgent')).not.toBeNull();

    vi.advanceTimersByTime(4_000);
    fixture.detectChanges();
    expect(text('[data-timer]')).toBe('Revealing…');
    expect(card('Pump').disabled).toBe(true);
  });

  it('plays an untargeted card with Choose, locked in at once, and has no separate lock', () => {
    render();

    card('Pump').click();
    fixture.detectChanges();
    expect(card('Pump').getAttribute('aria-pressed')).toBe('true');
    expect(page.querySelector('[data-list="targets"]')).toBeNull();
    expect(page.querySelector('[data-lock]')).toBeNull();
    button('[data-choose]').click();

    expect(sent).toEqual([{ card: 'c7', lock: true }]);
  });

  it('asks for a target for a targeted card, offering only players still playing', () => {
    render(royaleState(), [member('Alice', 1), member('Bob', 2, { isYou: true }), member('Carol', 3, { hasLeft: true })]);

    card('RugPull').click();
    fixture.detectChanges();
    expect(text('[data-aim-hint]')).toBe('Tap a player at the table to aim Rug Pull.');
    const alice = (page.querySelector('[data-seat="1"] [data-target="1"]') as HTMLInputElement);
    const carol = (page.querySelector('[data-seat="3"] [data-target="3"]') as HTMLInputElement);
    expect(page.querySelector('[data-target="2"]')).toBeNull();
    expect(carol.disabled).toBe(true);
    expect(page.querySelector('[data-seat="3"]')?.classList).toContain('left');
    expect(page.querySelector('[data-seat="3"]')?.classList).not.toContain('aimable');
    expect(page.querySelector('[data-seat="1"]')?.classList).toContain('aimable');
    expect(text('[data-seat="1"] label')).toBe('Aim Rug Pull at Alice (115 MON)');
    expect(button('[data-choose]').disabled).toBe(true);

    // Tapping the seat anywhere picks it: the label covers the seat.
    (page.querySelector('[data-seat="1"] label') as HTMLLabelElement).click();
    fixture.detectChanges();
    expect(alice.checked).toBe(true);
    expect(page.querySelector('[data-seat="1"]')?.classList).toContain('aimed');
    expect(text('[data-aim-hint]')).toBe('Rug Pull aimed at Alice. Tap another player to change.');
    button('[data-choose]').click();

    expect(sent).toEqual([{ card: 'c2', target: 1, lock: true }]);
  });

  it('holds with HODL', () => {
    render();

    card('Hodl').click();
    fixture.detectChanges();
    button('[data-choose]').click();

    expect(sent).toEqual([{ hodl: true, lock: true }]);
  });

  it('returns to your pending action from the server, and keeps a pick when someone else submits', () => {
    render(royaleState({ action: { hodl: false, card: 'c2', target: 1 } }), defaultMembers({ hasSubmitted: true }));

    expect(card('RugPull').getAttribute('aria-pressed')).toBe('true');
    expect(page.querySelector<HTMLInputElement>('[data-target="1"]')?.checked).toBe(true);
    expect(text('[data-pending]')).toBe('You chose Rug Pull at Alice.');

    card('Pump').click();
    fixture.detectChanges();
    render(royaleState({ action: { hodl: false, card: 'c2', target: 1 } }), defaultMembers({ hasSubmitted: true }, { hasSubmitted: true }));
    expect(card('Pump').getAttribute('aria-pressed')).toBe('true');
  });

  it('allows nothing more once you have chosen', () => {
    render(royaleState({ action: { hodl: true } }), defaultMembers({ hasSubmitted: true, hasLocked: true }));

    expect(text('[data-pending]')).toBe('You chose HODL.');
    expect(card('Pump').disabled).toBe(true);
    expect(button('[data-choose]').disabled).toBe(true);
    expect(button('[data-use-ability]').disabled).toBe(true);
  });

  it('waits for the round to open before taking actions', () => {
    render(royaleState({ ledger: [] }), defaultMembers(), -4);

    expect(text('[data-timer]')).toBe('Decisions open in 4s');
    expect(card('Pump').disabled).toBe(true);
    expect(page.querySelector('[data-reveal]')).toBeNull();
  });

  it('announces the round in a dialog until decisions open', () => {
    render(royaleState({ ledger: [] }), defaultMembers(), -4);

    const dialog = page.querySelector('[data-announcement]') as HTMLDialogElement;
    expect(dialog.open).toBe(true);
    expect(text('[data-announcement] .announcement-round')).toBe('Round 2 of 5');
    expect(text('[data-announcement-event]')).toBe('Meme Hype');
    expect(text('#announcement-event')).toBe('Pumps gain 50% more.');
    expect(text('[data-announcement-timer]')).toBe('Decisions open in 4s');

    vi.advanceTimersByTime(4_000);
    fixture.detectChanges();
    expect(dialog.open).toBe(false);
    expect(page.querySelector('[data-announcement-event]')).toBeNull();
    expect(card('Pump').disabled).toBe(false);
  });

  it('closes the announcement early with its button, for that round only', () => {
    render(royaleState({ ledger: [] }), defaultMembers(), -4);

    button('[data-announcement-close]').click();
    fixture.detectChanges();
    expect((page.querySelector('[data-announcement]') as HTMLDialogElement).open).toBe(false);

    render(royaleState({ ledger: [], round: 3 }), defaultMembers(), -4);
    expect((page.querySelector('[data-announcement]') as HTMLDialogElement).open).toBe(true);
    expect(text('[data-announcement] .announcement-round')).toBe('Round 3 of 5');
  });

  it('shows no announcement while deciding or during the reveal', () => {
    render();
    expect((page.querySelector('[data-announcement]') as HTMLDialogElement).open).toBe(false);

    render(royaleState(), defaultMembers(), -12);
    expect((page.querySelector('[data-announcement]') as HTMLDialogElement).open).toBe(false);
  });

  it('reveals a round in a dialog with your own line in bold, until you close it or the next announcement', () => {
    render(royaleState(), defaultMembers(), -12);

    const dialog = page.querySelector('[data-reveal-dialog]') as HTMLDialogElement;
    expect(dialog.open).toBe(true);
    expect(text('[data-reveal-timer]')).toBe('Next round in 12s');
    const lines = Array.from(page.querySelectorAll('[data-reveal-line]'));
    expect(lines.map((line) => line.classList.contains('yours'))).toEqual([false, true]);
    expect(lines[1].textContent?.trim()).toBe('You didn’t act, so held (HODL) and lost 15 MON to attacks: −15 MON, now 85 MON.');

    button('[data-reveal-close]').click();
    fixture.detectChanges();
    expect(dialog.open).toBe(false);
    expect((page.querySelector('[data-announcement]') as HTMLDialogElement).open).toBe(false);

    vi.advanceTimersByTime(8_000);
    fixture.detectChanges();
    expect((page.querySelector('[data-announcement]') as HTMLDialogElement).open).toBe(true);
  });

  it('hands the reveal over to the announcement when its time is up', () => {
    render(royaleState(), defaultMembers(), -12);

    vi.advanceTimersByTime(8_000);
    fixture.detectChanges();

    expect((page.querySelector('[data-reveal-dialog]') as HTMLDialogElement).open).toBe(false);
    expect((page.querySelector('[data-announcement]') as HTMLDialogElement).open).toBe(true);
  });

  it('reveals the last round between rounds, in the order it resolved', () => {
    // Round 2 opens in 12 s: the reveal shows until its announcement, 5 s before.
    render(royaleState({ ledger: [{ ...(royaleState()['ledger'] as object[])[0], priority: [2, 1] }] }), defaultMembers(), -12);

    expect(text('[data-reveal] h3')).toBe('Round 1 revealed');
    expect(Array.from(page.querySelectorAll('[data-reveal-line]')).map((li) => li.textContent?.trim())).toEqual([
      'You didn’t act, so held (HODL) and lost 15 MON to attacks: −15 MON, now 85 MON.',
      'Alice played Rug Pull at You and stole 15 MON: +15 MON, now 115 MON.',
    ]);
    expect(text('[data-timer]')).toBe('Next round in 12s');

    vi.advanceTimersByTime(8_000);
    fixture.detectChanges();
    expect(page.querySelector('[data-reveal]')).toBeNull();
    expect(text('[data-timer]')).toBe('Decisions open in 4s');
  });

  it('reveals the last round once the match has ended, with a countdown to the results', () => {
    let results = 0;
    fixture.componentInstance.seeResults.subscribe(() => results++);
    const final = royaleState({ round: 1 });
    fixture.componentRef.setInput('finalRevealUntil', Date.now() + 20_000);
    render(final, defaultMembers({}, {}), 0);

    expect(text('[data-round]')).toBe('Game over');
    expect(text('[data-timer]')).toBe('Results in 20s');
    expect(text('[data-reveal] h3')).toBe('Round 1 revealed · the last round');
    expect(page.querySelectorAll('[data-reveal-line]').length).toBe(2);
    expect(page.querySelector('[data-list="hand"]')).toBeNull();
    expect(text('[data-seat="1"] [data-status]')).toBe('Final balance');
    expect((page.querySelector('[data-announcement]') as HTMLDialogElement).open).toBe(false);

    expect((page.querySelector('[data-reveal-dialog]') as HTMLDialogElement).open).toBe(true);
    expect(text('[data-reveal-timer]')).toBe('Results in 20s');

    vi.advanceTimersByTime(5_000);
    fixture.detectChanges();
    expect(text('[data-timer]')).toBe('Results in 15s');
    button('[data-see-results]').click();
    expect(results).toBe(1);

    // Escape closes the dialog: the results come at once too.
    (page.querySelector('[data-reveal-dialog]') as HTMLDialogElement).dispatchEvent(new Event('close'));
    expect(results).toBe(2);
  });

  it('uses your ability while deciding, then says it was used', () => {
    render();

    expect(text('[data-ability-panel]')).toContain(stopLoss.description);
    button('[data-use-ability]').click();
    expect(used).toEqual([{ abilityId: 'stop_loss' }]);
    expect(text('[data-uses-left]')).toBe('1/1 left');
    expect(page.querySelector('[data-ability-panel] [data-holder-badge]')).toBeNull();

    render(royaleState({ ability: 'stop_loss' }), defaultMembers({ ability: { ...stopLoss, usesLeft: 0 } }));
    expect(text('[data-ability-used]')).toBe('You used Stop Loss this round. The others see it at the reveal.');
    expect(page.querySelector('[data-use-ability]')).toBeNull();
  });

  it('shows the next round’s event only when Alpha Leak sent it', () => {
    render(royaleState({ ability: 'alpha_leak', nextEvent: 'MarketCrash' }));

    expect(text('[data-next-event]')).toBe('🔮 Alpha Leak: next round is Market Crash. Only you can see this.');
  });

  const insiderInfo: RoomAbility = {
    id: 'insider_info',
    name: 'Insider Info',
    description: 'Use while deciding. Only you see an opponent’s hand for the rest of the round; they learn you looked at the reveal. 2 uses per game, one a round.',
    target: 'Opponent',
    maxUses: 2,
    values: {},
    usesLeft: 2,
    holderOnly: true,
  };

  it('aims Insider Info at an opponent still playing, with the holder badge and uses left', () => {
    render(royaleState({ players: [{ seat: 1, mon: 115, cards: 3 }, { seat: 2, mon: 85, cards: 3 }] }), [
      member('Alice', 1),
      member('Bob', 2, { isYou: true, ability: insiderInfo }),
      member('Carol', 3, { hasLeft: true }),
    ]);

    expect(text('[data-ability-panel] [data-holder-badge]')).toBe('♛ Holder');
    expect(text('[data-uses-left]')).toBe('2/2 left');
    const buttons = Array.from(page.querySelectorAll<HTMLButtonElement>('[data-use-ability]'));
    expect(buttons.map((b) => b.textContent?.trim())).toEqual(['Insider Info on Alice']);
    buttons[0].click();
    expect(used).toEqual([{ abilityId: 'insider_info', target: 1 }]);
  });

  it('shows the hand Insider Info looked at only from the server’s view of it', () => {
    render(
      royaleState({ ability: 'insider_info', insider: { seat: 1, hand: [{ id: 'c4', card: 'RugPull' }, { id: 'c5', card: 'WhaleSwap' }, { id: 'c6', card: 'Pump' }] } }),
      defaultMembers({ ability: { ...insiderInfo, usesLeft: 1 } }),
    );

    expect(text('[data-insider] p')).toBe('🕵️ Insider Info: Alice holds');
    expect(Array.from(page.querySelectorAll('[data-insider] li')).map((li) => li.textContent?.trim())).toEqual(['🪤 Rug Pull', '🐋 Whale Swap', '📈 Pump']);
    expect(text('[data-insider] .hint')).toBe('Only you can see this, until the round ends. Not what they chose.');
    expect(text('[data-ability-used]')).toBe('You used Insider Info on Alice this round. The others see it at the reveal.');
    expect(page.querySelector('[data-use-ability]')).toBeNull();

    render(royaleState(), defaultMembers({ ability: { ...insiderInfo, usesLeft: 1 } }));
    expect(page.querySelector('[data-insider]')).toBeNull();
    expect(text('[data-uses-left]')).toBe('1/2 left');
  });

  it('keeps every earlier round in its history', () => {
    render();

    expect(text('[data-history] summary')).toBe('Earlier rounds');
    expect(text('[data-history] h4')).toBe('Round 1 · Calm Market');
  });
  describe('table (STORY-094)', () => {
    const six = () => [
      member('Alice', 1),
      member('Bob', 2),
      member('Carol', 3, { isYou: true, ability: stopLoss }),
      member('Dan', 4, { hasSubmitted: true, hasLocked: true }),
      member('Eve', 5, { hasLeft: true }),
      member('Finn', 6),
    ];
    const sixState = (overrides: Record<string, unknown> = {}) =>
      royaleState({
        players: [1, 2, 3, 4, 6].map((seat) => ({ seat, mon: 100, cards: seat === 6 ? 2 : 3 })),
        ledger: [],
        ...overrides,
      });

    it('seats you at the bottom and the others across the top, from the seat after yours', () => {
      render(sixState(), six());

      const opponents = Array.from(page.querySelectorAll('[data-list="royale-players"] [data-seat]'));
      expect(opponents.map((seat) => seat.getAttribute('data-seat'))).toEqual(['4', '5', '6', '1', '2']);
      expect(page.querySelector('.me [data-seat]')?.getAttribute('data-seat')).toBe('3');
      expect(text('.me [data-seat] .name')).toBe('Carol You');
      // The arc: the ends sit lowest, the middle highest.
      expect(opponents.map((seat) => (seat as HTMLElement).style.getPropertyValue('--arc'))).toEqual(['1', '0.5', '0', '0.5', '1']);
    });

    it('shows every seat’s avatar, MON, face-down cards and status, greying out a player who left', () => {
      render(sixState(), six());

      expect(page.querySelectorAll('[data-table] [data-seat] app-avatar-badge').length).toBe(6);
      expect(page.querySelectorAll('[data-seat="1"] [data-backs] .back').length).toBe(3);
      expect(page.querySelectorAll('[data-seat="6"] [data-backs] .back').length).toBe(2);
      expect(text('[data-seat="4"] [data-status]')).toBe('✓ Chosen · 3 cards');
      expect(text('[data-seat="1"] [data-status]')).toBe('Deciding… · 3 cards');
      expect(page.querySelector('[data-seat="5"]')?.classList).toContain('left');
      expect(text('[data-seat="5"] [data-status]')).toBe('Left');
      expect(page.querySelector('[data-seat="5"] [data-backs]')).toBeNull();
    });

    it('never shows an opponent’s card faces or pending action while deciding', () => {
      render(sixState({ action: { hodl: false, card: 'c7', target: null } }), six());

      const opponents = page.querySelector('[data-list="royale-players"]') as HTMLElement;
      expect(opponents.querySelectorAll('.face').length).toBe(0);
      expect(opponents.textContent).not.toMatch(/Pump|Rug Pull|Airdrop|Whale Swap|Diamond Hands|Chaos|HODL/);
    });

    it('draws your hand as cards with their name, icon and effect, and HODL', () => {
      render();

      const pump = card('Pump');
      expect(pump.querySelector('.card-name')?.textContent?.trim()).toBe('Pump');
      expect(pump.querySelector('.card-icon')?.textContent?.trim()).toBe('📈');
      expect(pump.querySelector('.card-text')?.textContent?.trim()).toBe('Gain 20 MON (more under Meme Hype, less in a Crash).');
      expect(pump.style.getPropertyValue('--accent')).not.toBe('');
      expect(text('[data-card="Hodl"] .card-name')).toBe('HODL');
    });

    it('can’t aim or pick once you have chosen or outside the decision window', () => {
      render(royaleState({ action: { hodl: false, card: 'c2', target: 1 } }), defaultMembers({ hasSubmitted: true, hasLocked: true }));

      expect(page.querySelector<HTMLInputElement>('[data-target="1"]')?.disabled).toBe(true);
      expect(page.querySelector('[data-seat="1"]')?.classList).not.toContain('aimable');
      expect(card('Airdrop').disabled).toBe(true);

      render(royaleState({ ledger: [] }), defaultMembers(), -4);
      expect(card('RugPull').disabled).toBe(true);
    });

    it('turns face up only the hand your Insider Info shows you, on that seat', () => {
      render(royaleState({ ability: 'insider_info', insider: { seat: 1, hand: [{ id: 'c4', card: 'RugPull' }, { id: 'c5', card: 'Chaos' }] } }));

      expect(Array.from(page.querySelectorAll('[data-seat="1"] [data-peek] .face')).map((c) => c.getAttribute('title'))).toEqual(['Rug Pull', 'Chaos']);
      expect(page.querySelector('[data-seat="1"] [data-backs]')).toBeNull();
    });
  });

  describe('animated reveal (STORY-095)', () => {
    /** Plays round 1 from the decision, then the snapshot that resolves it, `late` ms after it resolved. */
    function playThenResolve(late = 0, resolved: Record<string, unknown> = royaleState()): void {
      render(royaleState({ round: 1, ledger: [] }));
      vi.advanceTimersByTime(late);
      // The next round opens 20 s after the resolution: 15 s of reveal and transition and 5 s of announcement.
      render(resolved, defaultMembers(), -(20 - late / 1000));
    }

    const seatMon = (seat: number) => text(`[data-seat="${seat}"] [data-mon]`);
    const dialogOpen = () => (page.querySelector('[data-reveal-dialog]') as HTMLDialogElement).open;

    it('plays a watched round on the table in resolution order, then opens the reveal dialog', () => {
      playThenResolve();

      // Alice resolves first: her Rug Pull is face up and flies; Bob's card is still face down and his MON unchanged.
      expect(page.querySelector('[data-seat="1"] [data-played-card]')?.getAttribute('data-played-card')).toBe('RugPull');
      expect(page.querySelector('[data-seat="2"] [data-played-card]')).toBeNull();
      expect(page.querySelector('[data-seat="2"] .played .back')).not.toBeNull();
      expect(seatMon(1)).toBe('100 MON');
      expect(seatMon(2)).toBe('100 MON');
      expect(dialogOpen()).toBe(false);
      expect(page.querySelectorAll('[data-reveal-text] li').length).toBe(2);

      // The Rug Pull lands on Bob.
      vi.advanceTimersByTime(400);
      fixture.detectChanges();
      expect(text('[data-seat="2"] [data-impact]')).toBe('Hit');
      expect(page.querySelector('[data-flying]')).toBeNull();

      // Bob's turn: he held by default; his MON counts down to the server's.
      vi.advanceTimersByTime(100);
      fixture.detectChanges();
      expect(page.querySelector('[data-seat="2"] [data-played-card]')?.getAttribute('data-played-card')).toBe('Hodl');

      vi.advanceTimersByTime(700);
      fixture.detectChanges();
      expect(seatMon(1)).toBe('115 MON');
      expect(seatMon(2)).toBe('85 MON');
      expect(text('[data-seat="1"] [data-change]')).toBe('+15');
      expect(text('[data-seat="2"] [data-change]')).toBe('−15');
      expect(page.querySelector('[data-seat="1"] [data-status]')).toBeNull();
      expect(page.querySelector('[data-reveal-text]')).toBeNull();

      // The table holds the settled reveal a moment before the dialog opens.
      expect(dialogOpen()).toBe(false);
      vi.advanceTimersByTime(REVEAL_HOLD_MS - 100);
      fixture.detectChanges();
      expect(dialogOpen()).toBe(false);
      vi.advanceTimersByTime(300);
      fixture.detectChanges();
      expect(dialogOpen()).toBe(true);
    });

    it('sends the card flying while it is in the air', () => {
      playThenResolve();
      fixture.detectChanges();

      expect(page.querySelectorAll('[data-flying]').length).toBe(1);
      expect(text('[data-flying]')).toBe('🪤');
    });

    it('shows the final state at once on a first snapshot, as after a reload mid-reveal', () => {
      render(royaleState(), defaultMembers(), -19);

      expect(page.querySelector('[data-seat="2"] [data-played-card]')?.getAttribute('data-played-card')).toBe('Hodl');
      expect(seatMon(2)).toBe('85 MON');
      expect(text('[data-seat="2"] [data-impact]')).toBe('Hit');
      expect(dialogOpen()).toBe(true);
    });

    it('shows the final state at once when the resolved round arrives late, as after a reconnect', () => {
      playThenResolve(5_000);

      expect(seatMon(2)).toBe('85 MON');
      expect(page.querySelector('[data-reveal-text]')).toBeNull();
      expect(dialogOpen()).toBe(true);
    });

    it('shows everything at once under reduced motion', () => {
      withoutMotion();
      playThenResolve();

      expect(seatMon(1)).toBe('115 MON');
      expect(page.querySelector('[data-seat="2"] [data-played-card]')).not.toBeNull();
      expect(page.querySelector('[data-flying]')).toBeNull();
      expect(dialogOpen()).toBe(true);
    });

    it('plays on through another snapshot of the same round', () => {
      playThenResolve();
      render(royaleState(), defaultMembers({}, { hasLeft: true }), -19.9);

      expect(seatMon(2)).toBe('100 MON');
      expect(dialogOpen()).toBe(false);
    });

    it('lets a snapshot of a later round replace the animation instead of queuing behind it', () => {
      playThenResolve();
      expect(seatMon(2)).toBe('100 MON');

      const [first] = royaleState()['ledger'] as { round: number }[];
      render(royaleState({ round: 3, ledger: [first, { ...first, round: 2 }] }), defaultMembers(), -10);
      expect(seatMon(2)).toBe('85 MON');
      expect(page.querySelector('[data-reveal-text]')).toBeNull();
      expect(text('[data-reveal] h3')).toBe('Round 2 revealed');
    });

    it('reveals the last round on the table when the match ends', () => {
      render(royaleState({ round: 1, ledger: [] }));
      fixture.componentRef.setInput('finalRevealUntil', Date.now() + 10_000);
      render(royaleState({ round: 1 }), defaultMembers(), 0);

      expect(seatMon(2)).toBe('100 MON');
      expect(dialogOpen()).toBe(false);
      vi.advanceTimersByTime(1_200);
      fixture.detectChanges();
      expect(seatMon(2)).toBe('85 MON');
      expect(text('[data-seat="2"] [data-status]')).toBe('Final balance');
      // The clock ticks every 250 ms once the cards have settled.
      vi.advanceTimersByTime(REVEAL_HOLD_MS + 250);
      fixture.detectChanges();
      expect(dialogOpen()).toBe(true);
    });

    it('shows an ability by name with its target', () => {
      const [round] = royaleState()['ledger'] as { entries: object[] }[];
      const ledger = [{ ...round, entries: [{ ...round.entries[0], ability: 'insider_info', abilityTarget: 2 }, round.entries[1]] }];
      playThenResolve(0, royaleState({ ledger }));

      expect(text('[data-seat="1"] [data-reveal-ability]')).toBe('Insider Info on You');
    });

    it('doesn’t animate a line it doesn’t know, which shows with its text at once', () => {
      const [round] = royaleState()['ledger'] as { entries: object[] }[];
      const ledger = [{ ...round, entries: [{ ...round.entries[0], ability: 'time_travel' }, round.entries[1]] }];
      playThenResolve(0, royaleState({ ledger }));

      // Alice's line isn't animated; Bob's is, and goes first.
      expect(seatMon(1)).toBe('115 MON');
      expect(page.querySelector('[data-seat="2"] [data-played-card]')?.getAttribute('data-played-card')).toBe('Hodl');
      vi.advanceTimersByTime(800 + REVEAL_HOLD_MS);
      fixture.detectChanges();
      expect(dialogOpen()).toBe(true);
      expect(Array.from(page.querySelectorAll('[data-reveal-line]')).map((li) => li.textContent?.trim())[0]).toContain('used time_travel');
    });
  });
});
