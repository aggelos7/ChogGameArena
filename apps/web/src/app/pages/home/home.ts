import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-home',
  imports: [RouterLink],
  template: `
    <h1>Chog Game Arena</h1>
    <p>Multiplayer board games for the Chog community.</p>
    <a routerLink="/games">Browse games</a>
  `,
})
export class Home {}
