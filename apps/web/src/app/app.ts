import { Component, ElementRef, Injector, afterNextRender, effect, inject, untracked, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, skip } from 'rxjs';
import { PlayerSession } from './identity/player-session';
import { AvatarBadge } from './profile/avatar-badge';
import { ProfileApi } from './profile/profile-api';
import { CurrentRoomBanner } from './rooms/current-room-banner';
import { ErrorState } from './shared/ui/state-views';
import { NavigationStatus } from './shell/navigation-status';

@Component({
  imports: [RouterLink, RouterLinkActive, RouterOutlet, ErrorState, CurrentRoomBanner, AvatarBadge],
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly session = inject(PlayerSession);
  protected readonly navigation = inject(NavigationStatus);
  private readonly main = viewChild.required<ElementRef<HTMLElement>>('main');

  constructor() {
    const injector = inject(Injector);
    const profiles = inject(ProfileApi);
    // The header shows the player's avatar, which only their profile has. Load it for each player the session switches
    // to (guest, wallet sign-in, sign-out); until then, and if it can't be loaded, the header shows the name alone.
    effect(() => {
      const playerId = this.session.player()?.playerId;
      untracked(() => {
        this.session.updateAvatar(null);
        if (playerId) {
          profiles.get().subscribe({
            next: (profile) => {
              if (this.session.player()?.playerId === playerId) {
                this.session.updateAvatar(profile.avatar);
              }
            },
            error: () => undefined,
          });
        }
      });
    });
    // After an in-app navigation, move focus to the new page so keyboard and screen reader users
    // start at its content instead of the link they just activated. The first load is left alone.
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationEnd),
        skip(1),
        takeUntilDestroyed(),
      )
      .subscribe(() => afterNextRender(() => this.focusMain(), { injector }));
  }

  /** The skip link focuses `<main>` directly; a `#fragment` href would resolve against `<base href>`. */
  protected skipToMain(event: Event): void {
    event.preventDefault();
    this.focusMain();
  }

  private focusMain(): void {
    this.main().nativeElement.focus({ preventScroll: true });
  }
}
