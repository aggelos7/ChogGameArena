import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  NavigationEnd,
  NavigationError,
  Router,
  withNavigationErrorHandler,
  withRouterConfig,
} from '@angular/router';
import { filter } from 'rxjs';

/** Full page load of an app URL; a token so tests can observe it. */
export const PAGE_LOAD = new InjectionToken<(url: string) => void>('PAGE_LOAD', {
  providedIn: 'root',
  factory: () => {
    const document = inject(DOCUMENT);
    return (url) => document.location.assign(url);
  },
});

/**
 * Router-level loading and failure state for the shell. A page whose code cannot be downloaded
 * (e.g. a flaky mobile connection) surfaces as an error with a retry instead of a dead click.
 */
@Injectable({ providedIn: 'root' })
export class NavigationStatus {
  private readonly router = inject(Router);
  private readonly load = inject(PAGE_LOAD);

  readonly navigating = computed(() => this.router.currentNavigation() !== null);
  readonly failedUrl = signal<string | null>(null);

  constructor() {
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.failedUrl.set(null));
  }

  fail(error: NavigationError): void {
    this.failedUrl.set(error.url);
  }

  /**
   * Loads the failed URL from the server. A full load is used because browsers cache a failed
   * dynamic import, so an in-app retry of the same lazy chunk would fail again.
   */
  retry(): void {
    const url = this.failedUrl();
    if (url !== null) {
      this.load(this.router.serializeUrl(this.router.parseUrl(url)));
    }
  }
}

/**
 * Router features that hand navigation failures to `NavigationStatus`: the current page stays,
 * the shell shows the error, and `navigateByUrl` resolves `false` instead of rejecting.
 */
export function withNavigationErrorState() {
  return [
    withNavigationErrorHandler((error) => inject(NavigationStatus).fail(error)),
    withRouterConfig({ resolveNavigationPromiseOnError: true }),
  ] as const;
}
