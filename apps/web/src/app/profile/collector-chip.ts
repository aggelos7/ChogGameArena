import { Component, computed, input } from '@angular/core';
import { CollectorBadge } from './profile-api';

/**
 * Colours of the collector tiers (DEC-065), for the chip and the avatar frame; white text stays readable on each. A tier
 * the client doesn't know yet gets the neutral colour.
 */
export const COLLECTOR_COLORS: Readonly<Record<string, string>> = {
  holder: '#9a3412',
  collector: '#475569',
  patron: '#a16207',
  whale: '#0e7490',
};

const NEUTRAL = '#52525b';

export function collectorColor(badge: CollectorBadge): string {
  return COLLECTOR_COLORS[badge.id] ?? NEUTRAL;
}

/** A collector badge: the tier name on its colour. Purely cosmetic (STORY-060). */
@Component({
  selector: 'app-collector-chip',
  template: `<span class="chip" [style.background]="color()" [attr.data-collector]="badge().id"
    [attr.title]="'Collector tier: ' + badge().name">{{ badge().name }}</span>`,
  styles: `
    .chip {
      display: inline-block;
      padding: 0.0625rem 0.5rem;
      border-radius: 999px;
      color: #fff;
      font-size: 0.75rem;
      font-weight: 600;
      white-space: nowrap;
    }
  `,
})
export class CollectorChip {
  readonly badge = input.required<CollectorBadge>();

  protected readonly color = computed(() => collectorColor(this.badge()));
}
