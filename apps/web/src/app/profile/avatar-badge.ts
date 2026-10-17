import { Component, computed, input } from '@angular/core';
import { collectorColor } from './collector-chip';
import { Avatar, CollectorBadge } from './profile-api';

/** Placeholder avatar: the player's initial on the avatar colour, framed in their collector tier's colour if any. */
@Component({
  selector: 'app-avatar-badge',
  template: `<span class="badge" [style.background]="avatar().color" [style.width.rem]="size()" [style.height.rem]="size()"
    [style.font-size.rem]="size() / 2" [style.box-shadow]="ring()" [attr.data-frame]="frame()?.id" aria-hidden="true"
    >{{ initial() }}</span>`,
  styles: `
    .badge {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      border-radius: 50%;
      color: #fff;
      font-weight: 700;
      flex-shrink: 0;
    }
  `,
})
export class AvatarBadge {
  readonly avatar = input.required<Avatar>();
  readonly name = input.required<string>();
  readonly size = input(3);
  /** The collector tier whose frame to draw (DEC-065); none when null. */
  readonly frame = input<CollectorBadge | null>(null);

  protected readonly initial = computed(() => this.name().trim().charAt(0).toUpperCase());

  /** A gap in the page colour, then the tier's ring, so the frame shows on any avatar colour. */
  protected readonly ring = computed(() => {
    const frame = this.frame();
    return frame ? `0 0 0 2px var(--surface, #fff), 0 0 0 4px ${collectorColor(frame)}` : null;
  });
}
