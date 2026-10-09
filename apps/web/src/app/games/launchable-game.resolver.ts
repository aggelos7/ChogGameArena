import { inject } from '@angular/core';
import { RedirectCommand, ResolveFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { GameCatalog, GameSummary } from './game-catalog';

/**
 * Resolves the game for `/games/:gameId` only when the server reports it as Available.
 * Unknown, Coming Soon or Maintenance games, and catalogue errors, redirect to `/games`,
 * so a disabled game cannot be launched by typing its URL.
 */
export const launchableGameResolver: ResolveFn<GameSummary> = (route) => {
  const router = inject(Router);
  const backToCatalogue = new RedirectCommand(router.parseUrl('/games'));
  const gameId = route.paramMap.get('gameId');

  return inject(GameCatalog)
    .getGames()
    .pipe(
      map((games) => games.find((game) => game.id === gameId && game.status === 'Available') ?? backToCatalogue),
      catchError(() => of(backToCatalogue)),
    );
};
