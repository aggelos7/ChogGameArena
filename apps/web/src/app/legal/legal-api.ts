import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/**
 * The version of the terms of use this app's text is (STORY-069, DEC-070): the date they last changed. Change it with
 * the text, and the server's `TermsOfUse.CurrentVersion` with it, so players accept the new terms before their next
 * upload.
 */
export const TERMS_VERSION = '2026-10-19';

/** Mirrors `LegalResponse` from `GET /api/legal`. */
export interface Legal {
  termsVersion: string;
  /** Where players send questions and deletion requests; null when the deployment set none. */
  contactEmail: string | null;
}

@Injectable({ providedIn: 'root' })
export class LegalApi {
  private readonly http = inject(HttpClient);

  get(): Observable<Legal> {
    return this.http.get<Legal>('/api/legal');
  }
}
