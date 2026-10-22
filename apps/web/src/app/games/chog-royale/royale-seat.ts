import { Component, input, output } from '@angular/core';
import { AvatarBadge } from '../../profile/avatar-badge';
import { Avatar } from '../../profile/profile-api';
import { RoyaleCard } from './royale-board-model';

/** One player as the board lists them. */
export interface RoyaleSeat {
  seat: number;
  name: string;
  isYou: boolean;
  avatar: Avatar;
  mon: number;
  cards: number;
  status: 'locked' | 'submitted' | 'deciding' | 'left' | 'final';
}

/** A seat at the table: the player, and during a reveal what they played and what landed on them (STORY-094, STORY-095). */
export interface TableSeat extends RoyaleSeat {
  /** Where the seat sits on the arc: 0 at the top's middle, 1 at its ends. */
  arc: number;
  /** Your Insider Info's look at this player's hand, face up, only in your view (DEC-095). */
  peek: TableCard[] | null;
  reveal: SeatReveal | null;
}

export interface SeatReveal {
  /** The card played, face up once its turn in the reveal has come. */
  card: TableCard | null;
  ability: string | null;
  /** What cards aimed at this seat did, once they landed. */
  impacts: string[];
  change: string | null;
  gain: boolean;
  loss: boolean;
}

/** A card face as the table draws it; HODL is `Hodl`. */
export interface TableCard {
  id: RoyaleCard | 'Hodl';
  name: string;
  icon: string;
  accent: string;
}

/**
 * A seat at the Chog Royale table (STORY-094, DEC-097): the player's avatar, name and MON, their cards face down (or
 * face up only as your Insider Info shows them) and whether they have chosen, never what; during a reveal, what they
 * played and what landed on them (STORY-095). While you aim a targeted card, an opponent's seat is a labelled radio
 * that the whole seat takes taps for.
 */
@Component({
  selector: 'app-royale-seat',
  templateUrl: './royale-seat.html',
  styleUrl: './royale-seat.scss',
  imports: [AvatarBadge],
  host: {
    class: 'seat',
    role: 'listitem',
    '[class.you]': 'seat().isYou',
    '[class.left]': "seat().status === 'left'",
    '[class.aimable]': "!!aimWith() && canAim() && seat().status !== 'left'",
    '[class.aimed]': 'aimed()',
    '[style.--arc]': 'seat().arc',
    '[attr.data-seat]': 'seat().seat',
  },
})
export class RoyaleSeatView {
  readonly seat = input.required<TableSeat>();
  /** The name of the card you are aiming, when this opponent's seat can take it. */
  readonly aimWith = input<string | null>(null);
  readonly aimed = input(false);
  /** You may aim now: the round is open and you haven't chosen. */
  readonly canAim = input(false);
  readonly aim = output<void>();

  /** 0 to `count - 1`, for drawing `count` face-down cards. */
  protected range(count: number): number[] {
    return Array.from({ length: count }, (_, index) => index);
  }
}
