import { Component, OnInit, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { RoomError, RoomsApi, normalizeRoomCode, roomErrorMessage, toRoomError } from '../../rooms/rooms-api';
import { LoadingState } from '../../shared/ui/state-views';

/**
 * Invite link target (`/join/:code`): joins the room, then opens it. Joining again is harmless, so reopening the
 * link or pressing back is safe.
 */
@Component({
  selector: 'app-join-room',
  imports: [RouterLink, LoadingState],
  template: `
    <h1>Join room {{ normalized() }}</h1>
    @if (error(); as failure) {
      <div class="state-error" role="alert" data-error="join">
        <p>{{ message(failure) }}</p>
        @if (failure.roomCode) {
          <a [routerLink]="['/rooms', failure.roomCode]">Open room {{ failure.roomCode }}</a>
        }
      </div>
      <a class="back" routerLink="/games">Back to games</a>
    } @else {
      <app-loading-state message="Joining room…" />
    }
  `,
  styles: `
    .state-error {
      color: var(--danger);
    }

    a {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap-target);
    }
  `,
})
export class JoinRoomPage implements OnInit {
  private readonly api = inject(RoomsApi);
  private readonly router = inject(Router);

  readonly code = input.required<string>();

  protected readonly error = signal<RoomError | null>(null);
  protected readonly message = roomErrorMessage;

  protected normalized(): string {
    return normalizeRoomCode(this.code());
  }

  ngOnInit(): void {
    this.api.join(this.normalized()).subscribe({
      next: (room) => this.router.navigate(['/rooms', room.code], { replaceUrl: true }),
      error: (error: unknown) => this.error.set(toRoomError(error)),
    });
  }
}
