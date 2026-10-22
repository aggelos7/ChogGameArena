import { royaleState } from '../../testing';
import { RoyaleLedgerEntry, describeEntry, signedMon, toRoyaleBoard } from './royale-board-model';

describe('Chog Royale board model', () => {
  const names: Record<number, string> = { 1: 'Alice', 2: 'You', 3: 'Carol' };
  const name = (seat: number) => names[seat];

  function entry(extra: Partial<RoyaleLedgerEntry>): RoyaleLedgerEntry {
    return { seat: 1, card: null, target: null, byDefault: false, outcome: null, amount: 0, defense: null, lost: 0, market: 0, change: 0, balance: 100, ability: null, abilityTarget: null, ...extra };
  }

  it('reads the view the server sends, with nulls for what it leaves out', () => {
    const board = toRoyaleBoard(royaleState({ action: { hodl: false, card: 'c2', target: 2 }, ability: 'alpha_leak', nextEvent: 'MarketCrash' }));

    expect(board).not.toBeNull();
    expect(board?.round).toBe(2);
    expect(board?.hand.map((c) => c.card)).toEqual(['Pump', 'RugPull', 'Airdrop']);
    expect(board?.action).toEqual({ hodl: false, card: 'c2', target: 2 });
    expect([board?.ability, board?.nextEvent]).toEqual(['alpha_leak', 'MarketCrash']);
    expect(board?.ledger[0].entries[1]).toEqual(entry({ seat: 2, byDefault: true, lost: 15, change: -15, balance: 85 }));
    expect(board?.ledger[0].chaos).toBeNull();
  });

  it('reads a view without a pending action or ability', () => {
    const board = toRoyaleBoard(royaleState());

    expect([board?.action, board?.ability, board?.nextEvent]).toEqual([null, null, null]);
  });

  it('refuses anything it doesn’t understand', () => {
    for (const bad of [
      null,
      'nope',
      royaleState({ event: 'Moon' }),
      royaleState({ round: 0 }),
      royaleState({ hand: [{ id: 'c1', card: 'Moon' }] }),
      royaleState({ players: [{ seat: 1, mon: -1, cards: 3 }] }),
      royaleState({ action: { card: 7 } }),
      royaleState({ ledger: [{ round: 1, event: 'CalmMarket', priority: [1], entries: [{ seat: 1 }] }] }),
      royaleState({ nextEvent: 'Moon' }),
    ]) {
      expect(toRoyaleBoard(bad)).toBeNull();
    }
  });

  it('describes who played what at whom, blocks, swaps, the market and the change', () => {
    expect(describeEntry(entry({ card: 'Pump', outcome: 'Applied', amount: 30, change: 30, balance: 130 }), name)).toBe(
      'Alice played Pump (+30 MON): +30 MON, now 130 MON.',
    );
    expect(describeEntry(entry({ card: 'RugPull', target: 3, outcome: 'Blocked' }), name)).toBe(
      'Alice played Rug Pull at Carol, but it was blocked: ±0 MON, now 100 MON.',
    );
    expect(describeEntry(entry({ seat: 2, card: 'DiamondHands', outcome: 'Applied', amount: 5, defense: 'Blocked', change: 5, balance: 105 }), name)).toBe(
      'You played Diamond Hands (+5 MON) and blocked an attack: +5 MON, now 105 MON.',
    );
    expect(describeEntry(entry({ card: 'WhaleSwap', target: 2, outcome: 'Applied', amount: -4, change: -4, balance: 96 }), name)).toBe(
      'Alice played Whale Swap at You and swapped (−4 MON): −4 MON, now 96 MON.',
    );
    expect(describeEntry(entry({ seat: 3, byDefault: true, lost: 15, market: 5, change: -10, balance: 90 }), name)).toBe(
      'Carol didn’t act, so held (HODL), lost 15 MON to attacks and got 5 MON from the market: −10 MON, now 90 MON.',
    );
    expect(describeEntry(entry({ ability: 'stop_loss', card: 'RugPull', target: 3, outcome: 'Stopped' }), name)).toBe(
      'Alice used Stop Loss and played Rug Pull at Carol, but Stop Loss stopped it: ±0 MON, now 100 MON.',
    );
  });

  it('reads the hand Insider Info shows and whom a reveal says was looked at', () => {
    const insider = { seat: 1, hand: [{ id: 'c4', card: 'RugPull' }] };
    expect(toRoyaleBoard(royaleState({ ability: 'insider_info', insider }))?.insider).toEqual(insider);
    expect(toRoyaleBoard(royaleState())?.insider).toBeNull();
    expect(toRoyaleBoard(royaleState({ insider: { seat: 1, hand: [{ id: 'c4', card: 'Joker' }] } }))).toBeNull();
    expect(toRoyaleBoard(royaleState({ insider: { seat: 0, hand: [] } }))).toBeNull();

    const name = (seat: number) => ['', 'Alice', 'Bob', 'Carol'][seat];
    expect(describeEntry(entry({ ability: 'insider_info', abilityTarget: 3 }), name)).toBe('Alice used Insider Info on Carol and held (HODL): ±0 MON, now 100 MON.');
    expect(describeEntry(entry({ ability: 'fresh_mint' }), name)).toBe('Alice used Fresh Mint and held (HODL): ±0 MON, now 100 MON.');
  });

  it('signs MON changes', () => {
    expect([signedMon(5), signedMon(-5), signedMon(0)]).toEqual(['+5 MON', '−5 MON', '±0 MON']);
  });
});
