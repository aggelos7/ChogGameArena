import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AvatarBadge } from './avatar-badge';
import { CollectorChip } from './collector-chip';
import { CollectorBadge } from './profile-api';

@Component({
  imports: [AvatarBadge, CollectorChip],
  template: `<app-avatar-badge [avatar]="avatar" name="Alice" [frame]="badge()" />
    @if (badge(); as current) {
      <app-collector-chip [badge]="current" />
    }`,
})
class Host {
  readonly avatar = { id: 'classic', name: 'Classic', color: '#5b2bd6' };
  readonly badge = signal<CollectorBadge | null>(null);
}

describe('Collector chip and avatar frame', () => {
  function render(badge: CollectorBadge | null) {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.badge.set(badge);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    return {
      chip: element.querySelector<HTMLElement>('[data-collector]'),
      avatar: element.querySelector<HTMLElement>('app-avatar-badge span') as HTMLElement,
    };
  }

  it('draws no frame and no chip without a tier', () => {
    const { chip, avatar } = render(null);

    expect(chip).toBeNull();
    expect(avatar.hasAttribute('data-frame')).toBe(false);
    expect(avatar.style.boxShadow).toBe('');
  });

  it('names the tier and frames the avatar in its colour', () => {
    const { chip, avatar } = render({ id: 'whale', name: 'Whale' });

    expect(chip?.textContent).toBe('Whale');
    expect(chip?.getAttribute('title')).toBe('Collector tier: Whale');
    expect(chip?.style.background).toContain('rgb(14, 116, 144)');
    expect(avatar.getAttribute('data-frame')).toBe('whale');
    expect(avatar.style.boxShadow).toContain('#0e7490');
  });

  it('shows a tier the client does not know yet in the neutral colour', () => {
    const { chip, avatar } = render({ id: 'kraken', name: 'Kraken' });

    expect(chip?.textContent).toBe('Kraken');
    expect(chip?.style.background).toContain('rgb(82, 82, 91)');
    expect(avatar.style.boxShadow).toContain('#52525b');
  });
});
