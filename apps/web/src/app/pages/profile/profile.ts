import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { PlayerSession } from '../../identity/player-session';
import { AvatarBadge } from '../../profile/avatar-badge';
import { ErrorState, LoadingState } from '../../shared/ui/state-views';
import { WalletPanel } from '../../wallet/wallet-panel';
import {
  Achievement,
  Avatar,
  DISPLAY_NAME_MAX,
  DISPLAY_NAME_MIN,
  DISPLAY_NAME_PATTERN,
  DISPLAY_NAME_RULE,
  Profile,
  ProfileApi,
} from '../../profile/profile-api';

function displayNameValidator(control: AbstractControl<string>): ValidationErrors | null {
  const value = (control.value ?? '').trim();
  const valid = value.length >= DISPLAY_NAME_MIN && value.length <= DISPLAY_NAME_MAX && DISPLAY_NAME_PATTERN.test(value);
  return valid ? null : { displayName: true };
}

@Component({
  selector: 'app-profile',
  imports: [ReactiveFormsModule, DatePipe, AvatarBadge, LoadingState, ErrorState, WalletPanel],
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

  protected avatarById(id: string): Avatar | undefined {
    return this.avatars().find((avatar) => avatar.id === id);
  }

  private show(profile: Profile): void {
    this.profile.set(profile);
    this.form.setValue({ displayName: profile.displayName, avatarId: profile.avatar.id });
    this.form.markAsPristine();
  }
}
