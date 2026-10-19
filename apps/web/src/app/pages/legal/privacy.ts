import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LegalContact } from './legal-contact';

/**
 * The privacy notice (STORY-069, DEC-070): what the app stores and why, drafted from what it actually does; the product
 * owner has it reviewed legally before the launch.
 */
@Component({
  selector: 'app-privacy',
  imports: [RouterLink, LegalContact],
  templateUrl: './privacy.html',
  styleUrl: './legal.scss',
})
export class PrivacyPage {}
