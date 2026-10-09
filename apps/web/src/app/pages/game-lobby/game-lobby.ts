import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GameSummary } from '../../games/game-catalog';

/** Placeholder until the shared lobby exists (E04). Only reachable for Available games. */
@Component({
  selector: 'app-game-lobby',
  imports: [RouterLink],
  template: `
    <h1>{{ game().name }}</h1>
    <p>The {{ game().name }} lobby is coming soon.</p>
    <a routerLink="/games">Back to games</a>
  `,
})
export class GameLobby {
  readonly game = input.required<GameSummary>();
}
