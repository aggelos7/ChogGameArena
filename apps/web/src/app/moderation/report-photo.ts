import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, inject, input, signal, viewChild } from '@angular/core';
import { ModerationApi, REPORT_REASONS, ReportReason } from './moderation-api';

/** What the player is told when a report isn't taken, by the problem's `code`. */
const REPORT_ERRORS: Record<string, string> = {
  photo_already_reported: 'You have already reported this photo.',
  report_own_photo: "You can't report your own photo.",
};

/**
 * A small "Report" button for another player's photo (STORY-069, DEC-070). It opens a fixed list of reasons; there is
 * no free text. One report per photo: after sending, or if it was already reported, the button stays gone.
 */
@Component({
  selector: 'app-report-photo',
  template: `
    @switch (state()) {
      @case ('closed') {
        <button type="button" class="report" data-report [attr.aria-label]="'Report ' + name() + '’s photo'" (click)="open()">
          Report
        </button>
      }
      @case ('done') {
        <span class="report-status" role="status" data-reported>{{ message() }}</span>
      }
      @default {
        <form class="report-form" (submit)="send($event)" [attr.aria-label]="'Report ' + name() + '’s photo'">
          <label>
            <span class="label">Why?</span>
            <select #reasonSelect name="reason" data-report-reason [disabled]="state() === 'sending'">
              @for (choice of reasons; track choice.value) {
                <option [value]="choice.value">{{ choice.label }}</option>
              }
            </select>
          </label>
          <button type="submit" data-send-report [disabled]="state() === 'sending'">Send</button>
          <button type="button" class="secondary" data-cancel-report [disabled]="state() === 'sending'" (click)="state.set('closed')">
            Cancel
          </button>
          @if (message(); as error) {
            <span class="report-error" role="alert" data-report-error>{{ error }}</span>
          }
        </form>
      }
    }
  `,
  styles: `
    :host {
      display: inline-flex;
      font-size: 0.8rem;
    }

    .report {
      padding: 0.1rem 0.5rem;
      font-size: inherit;
      background: transparent;
      color: inherit;
      border: 1px solid currentColor;
      border-radius: 999px;
      opacity: 0.75;
      cursor: pointer;
    }

    .report:hover,
    .report:focus-visible {
      opacity: 1;
    }

    .report-form {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.35rem;
    }

    .report-form button,
    .report-form select {
      font-size: inherit;
      padding: 0.15rem 0.45rem;
    }

    .label {
      margin-right: 0.25rem;
    }

    .report-error {
      flex-basis: 100%;
    }
  `,
})
export class ReportPhoto {
  /** Where the photo is served; reports go to that photo. */
  readonly photoUrl = input.required<string>();
  /** Whose photo it is, for the button's accessible name. */
  readonly name = input.required<string>();

  private readonly api = inject(ModerationApi);
  private readonly reasonSelect = viewChild<ElementRef<HTMLSelectElement>>('reasonSelect');

  protected readonly reasons = REPORT_REASONS;
  protected readonly state = signal<'closed' | 'choosing' | 'sending' | 'done'>('closed');
  protected readonly message = signal<string | null>(null);

  protected open(): void {
    this.message.set(null);
    this.state.set('choosing');
    queueMicrotask(() => this.reasonSelect()?.nativeElement.focus());
  }

  protected send(event: Event): void {
    event.preventDefault();
    const reason = (this.reasonSelect()?.nativeElement.value ?? REPORT_REASONS[0].value) as ReportReason;
    this.state.set('sending');
    this.message.set(null);
    this.api.report(this.photoUrl(), reason).subscribe({
      next: () => this.finish('Reported. Thanks, an operator will check it.'),
      error: (error: unknown) => {
        const code = error instanceof HttpErrorResponse ? (error.error?.code as string | undefined) : undefined;
        if (code && REPORT_ERRORS[code]) {
          this.finish(REPORT_ERRORS[code]);
          return;
        }

        this.message.set(
          error instanceof HttpErrorResponse && error.status === 429
            ? 'Too many reports. Try again in a few minutes.'
            : error instanceof HttpErrorResponse && error.status === 404
              ? 'This photo is gone already.'
              : 'Your report was not sent. Try again.',
        );
        this.state.set('choosing');
      },
    });
  }

  private finish(message: string): void {
    this.message.set(message);
    this.state.set('done');
  }
}
