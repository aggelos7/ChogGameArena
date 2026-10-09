import { Component, computed, input } from '@angular/core';
import { Avatar } from './profile-api';

/** Placeholder avatar: the player's initial on the avatar colour. */
@Component({
  selector: 'app-avatar-badge',
  template: `<span class="badge" [style.background]="avatar().color" [style.width.rem]="size()" [style.height.rem]="size()"
    [style.font-size.rem]="size() / 2" aria-hidden="true">{{ initial() }}</span>`,
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

  protected readonly initial = computed(() => this.name().trim().charAt(0).toUpperCase());
}
