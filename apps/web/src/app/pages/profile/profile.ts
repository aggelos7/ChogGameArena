import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable, forkJoin } from 'rxjs';
import { PlayerSession } from '../../identity/player-session';
import { AvatarBadge } from '../../profile/avatar-badge';
import { CollectorChip } from '../../profile/collector-chip';
import { ErrorState, LoadingState } from '../../shared/ui/state-views';
import { WalletPanel } from '../../wallet/wallet-panel';
import {
  Achievement,
  Avatar,
  DISPLAY_NAME_MAX,
  DISPLAY_NAME_MIN,
  DISPLAY_NAME_PATTERN,
  DISPLAY_NAME_RULE,
  PHOTO_MAX_BYTES,
  PHOTO_TYPES,
  Profile,
  ProfileApi,
} from '../../profile/profile-api';

/** What the player is told when the server refuses a photo, by the problem's `code`. */
const PHOTO_ERRORS: Record<string, string> = {
  avatar_too_large: 'That photo is too big. Choose one of 2 MB or less.',
  avatar_unsupported_type: 'Choose a JPEG, PNG or WebP photo.',
  avatar_not_an_image: "That file isn't a photo we can read. Choose a JPEG, PNG or WebP photo.",
  avatar_too_many_pixels: 'That photo has too many pixels. Choose a smaller one.',
};

function displayNameValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = (control.value ?? '').trim();
  const valid = value.length >= DISPLAY_NAME_MIN && value.length <= DISPLAY_NAME_MAX && DISPLAY_NAME_PATTERN.test(value);
  return valid ? null : { displayName: true };
}

@Component({
  selector: 'app-profile',
  imports: [RouterLink, ReactiveFormsModule, DatePipe, AvatarBadge, CollectorChip, LoadingState, ErrorState, WalletPanel],
  templateUrl: './profile.html',
  styleUrl: './profile.scss',
})
export class ProfilePage implements OnInit {
  private readonly api = inject(ProfileApi);
  private readonly session = inject(PlayerSession);

  protected readonly displayNameRule = DISPLAY_NAME_RULE;
  protected readonly profile = signal<Profile | null>(null);
  protected readonly avatars = signal<Avatar[]>([]);
  protected readonly achievements = signal<Achievement[]>([]);
  protected readonly unlockedCount = computed(() => this.achievements().filter((a) => a.unlockedAt).length);
  protected readonly loadState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly saveState = signal<'idle' | 'saving' | 'saved' | 'error'>('idle');
  protected readonly serverErrors = signal<Record<string, string[]>>({});
  protected readonly photoTypes = PHOTO_TYPES.join(',');
  protected readonly photoState = signal<'idle' | 'uploading' | 'removing'>('idle');
  protected readonly photoError = signal<string | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    displayName: ['', displayNameValidator],
    avatarId: [''],
  });

  constructor() {
    // Wallet sign-in or sign-out can switch the session to another player; show that player's profile.
    effect(() => {
      const playerId = this.session.player()?.playerId;
      const shown = untracked(this.profile)?.playerId;
      if (playerId && shown && playerId !== shown) {
        untracked(() => this.load());
      }
    });
  }

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loadState.set('loading');
    forkJoin({ profile: this.api.get(), avatars: this.api.avatars(), achievements: this.api.achievements() }).subscribe({
      next: ({ profile, avatars, achievements }) => {
        this.avatars.set(avatars);
        this.achievements.set(achievements);
        this.show(profile);
        this.loadState.set('ready');
      },
      error: () => this.loadState.set('error'),
    });
  }

  protected save(): void {
    const current = this.profile();
    if (!current || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { displayName, avatarId } = this.form.getRawValue();
    const changes = {
      ...(displayName.trim() !== current.displayName ? { displayName: displayName.trim() } : {}),
      ...(avatarId !== current.avatar.id ? { avatarId } : {}),
    };
    if (Object.keys(changes).length === 0) {
      this.saveState.set('saved');
      return;
    }

    this.saveState.set('saving');
    this.serverErrors.set({});
    this.api.update(changes).subscribe({
      next: (profile) => {
        this.show(profile);
        this.session.updateDisplayName(profile.displayName);
        this.saveState.set('saved');
      },
      error: (error: unknown) => {
        const errors = error instanceof HttpErrorResponse && error.status === 400 ? error.error?.errors : undefined;
        this.serverErrors.set(errors ?? {});
        this.saveState.set('error');
      },
    });
  }

  /** Uploads the chosen file. Type and size are checked here only to answer quickly; the server decides. */
  protected choosePhoto(event: Event): void {
    const input = event.target as HTMLInputElement;
    const photo = input.files?.[0];
    input.value = '';
    if (!photo) {
      return;
    }

    if (!PHOTO_TYPES.includes(photo.type)) {
      this.photoError.set(PHOTO_ERRORS['avatar_unsupported_type']);
      return;
    }

    if (photo.size > PHOTO_MAX_BYTES) {
      this.photoError.set(PHOTO_ERRORS['avatar_too_large']);
      return;
    }

    this.photoState.set('uploading');
    this.changePhoto(this.api.uploadPhoto(photo));
  }

  protected removePhoto(): void {
    this.photoState.set('removing');
    this.changePhoto(this.api.removePhoto());
  }

  protected avatarById(id: string): Avatar | undefined {
    return this.avatars().find((avatar) => avatar.id === id);
  }

  /** Shows the new photo without touching unsaved edits in the form. */
  private changePhoto(request: Observable<Profile>): void {
    this.photoError.set(null);
    request.subscribe({
      next: (profile) => {
        this.profile.set(profile);
        this.photoState.set('idle');
      },
      error: (error: unknown) => {
        const code = error instanceof HttpErrorResponse ? error.error?.code : undefined;
        const tooMany = error instanceof HttpErrorResponse && error.status === 429;
        this.photoError.set(
          tooMany
            ? 'Too many photo changes. Try again in a few minutes.'
            : (PHOTO_ERRORS[code] ?? 'Your photo was not changed. Try again.'),
        );
        this.photoState.set('idle');
      },
    });
  }

  private show(profile: Profile): void {
    this.profile.set(profile);
    this.form.setValue({ displayName: profile.displayName, avatarId: profile.avatar.id });
    this.form.markAsPristine();
  }
}
