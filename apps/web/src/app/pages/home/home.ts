import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-home',
  imports: [RouterLink],
  template: `
    <h1>Chog Game Arena</h1>
    <p>Multiplayer board games for the Chog community.</p>
    <a class="cta" routerLink="/games">Browse games</a>
  `,
  styles: `
    .cta {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap-target);
      padding: 0.5rem 1.25rem;
      border-radius: 0.5rem;
      font-weight: 600;
      text-decoration: none;
      color: #fff;
      background: var(--brand);
    }
  `,
})
export class Home {}
