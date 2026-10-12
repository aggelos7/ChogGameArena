import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { catchError, filter, merge, of, switchMap } from 'rxjs';
import { PlayerSession } from '../identity/player-session';
import { Room, RoomsApi } from './rooms-api';

/**
 * Brings the player back to their open room (STORY-027, ENG-006, DEC-029): after a refresh, a closed tab or on another
 * device, every page but the room itself offers a way back. The server decides which room is open
 * (`GET /api/rooms/current`), checked again after each navigation and whenever the player changes (wallet sign-in).
 */
@Component({
  selector: 'app-current-room-banner',
  imports: [RouterLink],
  template: `
    @if (shown(); as room) {
      <aside class="current-room" aria-label="Your game" data-current-room>
        <p>
          @if (room.status === 'Active') {
            Your game in room <strong>{{ room.code }}</strong> is still on.
          } @else {
            You’re in room <strong>{{ room.code }}</strong>, waiting to start.
          }
        </p>
        <a [routerLink]="['/rooms', room.code]">{{ room.status === 'Active' ? 'Return to game' : 'Back to room' }}</a>
      </aside>
    }
  `,
  styles: `
    .current-room {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 0.25rem 1rem;
      padding: 0.25rem 1rem;
      background: var(--surface-muted);
      border-bottom: 1px solid var(--border);
    }

    p {
      margin: 0;
    }

    a {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap-target);
      padding: 0 1rem;
      border-radius: 0.5rem;
      font-weight: 600;
      text-decoration: none;
      color: #fff;
      background: var(--brand);
    }
  `,
})
export class CurrentRoomBanner {
  private readonly api = inject(RoomsApi);
  private readonly router = inject(Router);
  private readonly session = inject(PlayerSession);

  private readonly room = signal<Room | null>(null);
  private readonly url = signal(this.router.url);

  /** The open room, unless its own page is showing. */
  protected readonly shown = computed(() => {
    const room = this.room();
    const path = this.url().split(/[?#]/)[0].toUpperCase();
    return room && path !== `/ROOMS/${room.code}` ? room : null;
  });

  constructor() {
    const navigations = this.router.events.pipe(filter((event) => event instanceof NavigationEnd));
    merge(toObservable(this.session.player), navigations)
      .pipe(
        switchMap(() => {
          this.url.set(this.router.url);
          // Only once there is a session; a failed check hides the banner rather than showing a stale room.
          return this.session.player() ? this.api.current().pipe(catchError(() => of(null))) : of(null);
        }),
        takeUntilDestroyed(),
      )
      .subscribe((room) => this.room.set(room));
  }
}
