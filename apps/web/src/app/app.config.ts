import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { appRoutes } from './app.routes';
import { PlayerSession } from './identity/player-session';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withFetch()),
    provideRouter(appRoutes, withComponentInputBinding()),
    // Resolve the guest identity before the first render; failures leave the app usable.
    provideAppInitializer(() => inject(PlayerSession).ensureGuest()),
  ]
};
