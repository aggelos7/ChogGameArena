import { Component, OnInit, inject, signal } from '@angular/core';
import { LegalApi } from '../../legal/legal-api';

/**
 * The deployment's contact address for questions and deletion requests (`Legal:ContactEmail`, DEC-070), or a fallback
 * line when none is set or it can't be loaded.
 */
@Component({
  selector: 'app-legal-contact',
  template: `@if (email(); as address) {
      <a [href]="'mailto:' + address" data-contact>{{ address }}</a>
    } @else {
      <span data-contact-missing>the operator of this site</span>
    }`,
})
export class LegalContact implements OnInit {
  private readonly api = inject(LegalApi);

  protected readonly email = signal<string | null>(null);

  ngOnInit(): void {
    this.api.get().subscribe({ next: (legal) => this.email.set(legal.contactEmail), error: () => undefined });
  }
}
