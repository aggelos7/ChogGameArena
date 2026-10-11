import { Route } from '@angular/router';
import { launchableGameResolver } from './games/launchable-game.resolver';

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
  {
    path: 'games/:gameId',
    title: 'Lobby · Chog Game Arena',
    resolve: { game: launchableGameResolver },
    loadComponent: () => import('./pages/game-lobby/game-lobby').then((m) => m.GameLobby),
  },
  {
    path: 'join/:code',
    title: 'Join room · Chog Game Arena',
    loadComponent: () => import('./pages/join-room/join-room').then((m) => m.JoinRoomPage),
  },
  {
    path: 'rooms/:code',
    title: 'Room · Chog Game Arena',
    loadComponent: () => import('./pages/room/room').then((m) => m.RoomPage),
  },
  {
    path: 'chogs',
    title: 'My Chogs · Chog Game Arena',
    loadComponent: () => import('./pages/my-chogs/my-chogs').then((m) => m.MyChogsPage),
  },
  {
    path: 'profile',
    title: 'Profile · Chog Game Arena',
    loadComponent: () => import('./pages/profile/profile').then((m) => m.ProfilePage),
  },
  // Unknown URLs fall back to the home page instead of rendering an empty shell.
  { path: '**', redirectTo: '' },
];
