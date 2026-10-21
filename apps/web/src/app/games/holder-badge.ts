import { Component } from '@angular/core';

/**
 * Marks a holder-only ability, one only Chog holders may choose (STORY-091, DEC-078): a gold "Holder" badge with a
 * crown, beside the ability's name wherever it's shown.
 */
@Component({
  selector: 'app-holder-badge',
  template: `<span class="badge" title="Only Chog holders can choose this ability" data-holder-badge
    ><span aria-hidden="true">♛</span> Holder</span
  >`,
  styles: `
    // Centred on the name beside it, as the collector chip is.
    :host {
      display: inline-flex;
      align-items: center;
      vertical-align: middle;
      margin-left: 0.375rem;
    }
    .badge {
      display: inline-block;
      padding: 0.0625rem 0.5rem;
      border-radius: 999px;
      color: #fff;
      background: #a16207;
      font-size: 0.75rem;
      font-weight: 600;
      line-height: 1.4;
      white-space: nowrap;
      text-shadow: none;
    }
  `,
})
export class HolderBadge {}
