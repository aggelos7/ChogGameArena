/**
 * Pawn colours by seat, 1 to 6, for every game's board: distinct and readable with white text. The seat number is
 * printed on every pawn too, so colour is never the only cue.
 */
export const SEAT_COLORS: readonly string[] = ['#5b2bd6', '#0a6c8f', '#b4400b', '#13703a', '#b0175a', '#7a5c00'];

export function seatColor(seat: number): string {
  return SEAT_COLORS[(seat - 1) % SEAT_COLORS.length] ?? SEAT_COLORS[0];
}
