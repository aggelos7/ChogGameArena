import { Route } from '@angular/router';
import { launchableGameResolver } from './games/launchable-game.resolver';

export const appRoutes: Route[] = [
  {
    path: '',
    pathMatch: 'full',
    title: 'Chog Game Arena',
    loadComponent: () => import('./pages/home/home').then((m) => m.Home),
  },
  // The catalogue lives on the home page; keep old /games links working.
  { path: 'games', pathMatch: 'full', redirectTo: '' },
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
  // The leaderboard is called the ladder; keep old links working.
  { path: 'leaderboard', pathMatch: 'full', redirectTo: 'ladder' },
  {
    path: 'ladder',
    title: 'Ladder · Chog Game Arena',
    loadComponent: () => import('./pages/leaderboard/leaderboard').then((m) => m.LeaderboardPage),
  },
  {
    path: 'profile',
    title: 'Profile · Chog Game Arena',
    loadComponent: () => import('./pages/profile/profile').then((m) => m.ProfilePage),
  },
  {
    path: 'terms',
    title: 'Terms of use · Chog Game Arena',
    loadComponent: () => import('./pages/legal/terms').then((m) => m.TermsPage),
  },
  {
    path: 'privacy',
    title: 'Privacy · Chog Game Arena',
    loadComponent: () => import('./pages/legal/privacy').then((m) => m.PrivacyPage),
  },
  // Operators only; the API answers everyone else as if it didn't exist.
  {
    path: 'moderation',
    title: 'Reported photos · Chog Game Arena',
    loadComponent: () => import('./pages/moderation/moderation').then((m) => m.ModerationPage),
  },
  // Unknown URLs fall back to the home page instead of rendering an empty shell.
  { path: '**', redirectTo: '' },
];
