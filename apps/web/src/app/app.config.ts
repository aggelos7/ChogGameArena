import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch } from '@angular/common/http';
import {
  provideRouter,
  withComponentInputBinding,
  withInMemoryScrolling,
} from '@angular/router';
import { appRoutes } from './app.routes';
import { PlayerSession } from './identity/player-session';
import { withNavigationErrorState } from './shell/navigation-status';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withFetch()),
    provideRouter(
      appRoutes,
      withComponentInputBinding(),
      // New pages start at the top; back/forward restores the previous scroll position.
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled' }),
      ...withNavigationErrorState(),
    ),
    // Resolve the guest identity before the first render; failures leave the app usable.
    provideAppInitializer(() => inject(PlayerSession).ensureGuest()),
  ]
};
