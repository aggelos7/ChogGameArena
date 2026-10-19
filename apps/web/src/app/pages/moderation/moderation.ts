import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { ModerationApi, REPORT_REASONS, ReportedPhoto } from '../../moderation/moderation-api';
import { ErrorState, LoadingState } from '../../shared/ui/state-views';

/**
 * The operators' review of reported photos (STORY-069, DEC-070): each photo with reports, most reported first, to
 * remove or to dismiss. The server answers anyone else as if the page didn't exist.
 */
@Component({
  selector: 'app-moderation',
  imports: [DatePipe, LoadingState, ErrorState],
  templateUrl: './moderation.html',
  styleUrl: './moderation.scss',
})
export class ModerationPage implements OnInit {
  private readonly api = inject(ModerationApi);

  protected readonly reasons = REPORT_REASONS;
  protected readonly photos = signal<ReportedPhoto[]>([]);
  protected readonly loadState = signal<'loading' | 'ready' | 'error' | 'not-found'>('loading');
  /** The photo an action is running for. */
  protected readonly busy = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loadState.set('loading');
    this.api.reports().subscribe({
      next: (photos) => {
        this.photos.set(photos);
        this.loadState.set('ready');
      },
      error: (error: unknown) =>
        this.loadState.set(error instanceof HttpErrorResponse && error.status === 404 ? 'not-found' : 'error'),
    });
  }

  protected remove(photo: ReportedPhoto): void {
    this.act(photo, this.api.remove(photo.imageId), 'The photo was not removed. Try again.');
  }

  protected dismiss(photo: ReportedPhoto): void {
    this.act(photo, this.api.dismiss(photo.imageId), 'The reports were not dismissed. Try again.');
  }

  /** Either action takes the photo off the list; one already gone (404) leaves it too. */
  private act(photo: ReportedPhoto, request: Observable<void>, failure: string): void {
    this.busy.set(photo.imageId);
    this.actionError.set(null);
    request.subscribe({
      next: () => this.done(photo),
      error: (error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 404) {
          this.done(photo);
          return;
        }

        this.actionError.set(failure);
        this.busy.set(null);
      },
    });
  }

  private done(photo: ReportedPhoto): void {
    this.photos.update((photos) => photos.filter((p) => p.imageId !== photo.imageId));
    this.busy.set(null);
  }
}
