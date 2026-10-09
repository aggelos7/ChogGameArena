import { Component, input, output } from '@angular/core';

/** Announced, non-interactive progress message shown while content loads. */
@Component({
  selector: 'app-loading-state',
  template: `<p class="state" role="status"><span class="spinner" aria-hidden="true"></span>{{ message() }}</p>`,
  styleUrl: './state-views.scss',
})
export class LoadingState {
  readonly message = input('Loading…');
}

/** Failure message with a keyboard-reachable retry button. */
@Component({
  selector: 'app-error-state',
  template: `
    <div class="state state-error" role="alert">
      <p>{{ message() }}</p>
      <button type="button" (click)="retry.emit()">{{ retryLabel() }}</button>
    </div>
  `,
  styleUrl: './state-views.scss',
})
export class ErrorState {
  readonly message = input.required<string>();
  readonly retryLabel = input('Try again');
  readonly retry = output<void>();
}

/** Shown when a successful load returns nothing; projected content can offer a next step. */
@Component({
  selector: 'app-empty-state',
  template: `
    <div class="state state-empty">
      <p>{{ message() }}</p>
      <ng-content />
    </div>
  `,
  styleUrl: './state-views.scss',
})
export class EmptyState {
  readonly message = input.required<string>();
}
