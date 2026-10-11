import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { GameSummary } from '../../games/game-catalog';
import { ROOM_CODE_PATTERN, RoomError, RoomsApi, normalizeRoomCode, roomErrorMessage, toRoomError } from '../../rooms/rooms-api';

/** Start of a game: create a private room or join one with a code. Only reachable for Available games. */
@Component({
  selector: 'app-game-lobby',
  imports: [FormsModule, RouterLink],
  templateUrl: './game-lobby.html',
  styleUrl: './game-lobby.scss',
})
export class GameLobby {
  private readonly api = inject(RoomsApi);
  private readonly router = inject(Router);

  readonly game = input.required<GameSummary>();

  protected readonly creating = signal(false);
  protected readonly createError = signal<RoomError | null>(null);
  protected readonly code = signal('');
  protected readonly codeInvalid = signal(false);
  protected readonly message = roomErrorMessage;

  protected create(): void {
    this.creating.set(true);
    this.createError.set(null);
    this.api.create(this.game().id).subscribe({
      next: (room) => this.router.navigate(['/rooms', room.code]),
      error: (error: unknown) => {
        this.createError.set(toRoomError(error));
        this.creating.set(false);
      },
    });
  }

  protected join(): void {
    const code = normalizeRoomCode(this.code());
    if (!ROOM_CODE_PATTERN.test(code)) {
      this.codeInvalid.set(true);
      return;
    }
    this.codeInvalid.set(false);
    this.router.navigate(['/join', code]);
  }
}
