import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TERMS_VERSION } from '../../legal/legal-api';
import { LegalContact } from './legal-contact';

/**
 * The terms of use (STORY-069, DEC-070). Plain language, drafted from what the app does; the product owner has them
 * reviewed legally before the launch. Changing them means a new {@link TERMS_VERSION}.
 */
@Component({
  selector: 'app-terms',
  imports: [RouterLink, LegalContact],
  templateUrl: './terms.html',
  styleUrl: './legal.scss',
})
export class TermsPage {
  protected readonly version = TERMS_VERSION;
}
