import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, throwError } from 'rxjs';

/** Why a photo is reported: the only thing a reporter says (DEC-070). */
export type ReportReason = 'sexual' | 'hate' | 'violence' | 'scam' | 'other';

/** The reasons in the order players choose from, with their labels. */
export const REPORT_REASONS: readonly { value: ReportReason; label: string }[] = [
  { value: 'sexual', label: 'Sexual content' },
  { value: 'hate', label: 'Hate or harassment' },
  { value: 'violence', label: 'Violence' },
  { value: 'scam', label: 'Scam or spam' },
  { value: 'other', label: 'Something else' },
];

/** Mirrors `ReportedPhotoResponse` from `GET /api/moderation/reports`. Reporters are never named. */
export interface ReportedPhoto {
  imageId: string;
  imageUrl: string;
  ownerName: string;
  reporters: number;
  /** Reports by reason. */
  reasons: Partial<Record<ReportReason, number>>;
  firstReportedAt: string;
  lastReportedAt: string;
  /** Whether the reports hid it from other players. */
  hidden: boolean;
}

/** Only photos the API serves can be reported, so a report never goes anywhere else. */
const PHOTO_URL = /^\/api\/avatar-images\/[0-9a-f-]{36}$/;

/** Photo reports, for every player, and their review, for operators only (STORY-069, DEC-070). */
@Injectable({ providedIn: 'root' })
export class ModerationApi {
  private readonly http = inject(HttpClient);

  /** Reports the photo served at `photoUrl`, as found on a room member or a ladder entry. */
  report(photoUrl: string, reason: ReportReason): Observable<void> {
    if (!PHOTO_URL.test(photoUrl)) {
      return throwError(() => new Error(`Not a photo URL: ${photoUrl}`));
    }

    return this.http.post<void>(`${photoUrl}/reports`, { reason });
  }

  /** Answers 404 to anyone but an operator. */
  reports(): Observable<ReportedPhoto[]> {
    return this.http.get<ReportedPhoto[]>('/api/moderation/reports');
  }

  remove(imageId: string): Observable<void> {
    return this.http.delete<void>(`/api/moderation/photos/${encodeURIComponent(imageId)}`);
  }

  dismiss(imageId: string): Observable<void> {
    return this.http.post<void>(`/api/moderation/photos/${encodeURIComponent(imageId)}/dismiss`, null);
  }
}
