import { Route } from '@angular/router';

export const appRoutes: Route[] = [
  {
    path: '',
    pathMatch: 'full',
    title: 'Chog Game Arena',
    loadComponent: () => import('./pages/home/home').then((m) => m.Home),
  },
  {
    path: 'games',
    title: 'Games · Chog Game Arena',
    loadComponent: () => import('./pages/games/games').then((m) => m.Games),
  },
  // Unknown URLs fall back to the home page instead of rendering an empty shell.
  { path: '**', redirectTo: '' },
];
