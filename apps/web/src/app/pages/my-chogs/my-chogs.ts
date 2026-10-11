import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Characters, CharactersApi, SelectedCharacter } from '../../characters/characters-api';
import { PlayerSession } from '../../identity/player-session';
import { AvatarBadge } from '../../profile/avatar-badge';
import { EmptyState, ErrorState, LoadingState } from '../../shared/ui/state-views';

/** Gallery of the player's verified Chog NFTs and the standard Chogs, with the one character used next. */
@Component({
  selector: 'app-my-chogs',
  imports: [RouterLink, AvatarBadge, LoadingState, ErrorState, EmptyState],
  templateUrl: './my-chogs.html',
  styleUrl: './my-chogs.scss',
})
export class MyChogsPage implements OnInit {
  private readonly api = inject(CharactersApi);
  private readonly session = inject(PlayerSession);
  private shownPlayerId: string | undefined;

  protected readonly characters = signal<Characters | null>(null);
  protected readonly loadState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly selectState = signal<'idle' | 'saving' | 'saved' | 'error'>('idle');
  protected readonly selectError = signal('');
  protected readonly picked = signal<SelectedCharacter | null>(null);
  /** Token ids whose image failed to load; shown with the placeholder instead. */
  protected readonly brokenImages = signal<ReadonlySet<string>>(new Set());

  constructor() {
    // Wallet sign-in or sign-out switches the session to another player, who owns other Chogs.
    effect(() => {
      const playerId = this.session.player()?.playerId;
      if (playerId && this.shownPlayerId && playerId !== this.shownPlayerId) {
        untracked(() => this.load());
      }
    });
  }

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loadState.set('loading');
    this.selectState.set('idle');
    this.shownPlayerId = this.session.player()?.playerId;
    this.api.get().subscribe({
      next: (characters) => {
        this.characters.set(characters);
        this.picked.set(characters.selected);
        this.brokenImages.set(new Set());
        this.loadState.set('ready');
      },
      error: () => this.loadState.set('error'),
    });
  }

  /** The saved character, used for the next match. */
  protected isInUse(choice: SelectedCharacter): boolean {
    return sameCharacter(this.characters()?.selected, choice);
  }

  /** The character picked on the page, saved only with `save()`. */
  protected isPicked(choice: SelectedCharacter): boolean {
    return sameCharacter(this.picked(), choice);
  }

  protected pick(choice: SelectedCharacter): void {
    this.picked.set(choice);
    this.selectState.set('idle');
  }

  protected save(): void {
    const choice = this.picked();
    if (!choice || this.isInUse(choice)) {
      this.selectState.set('saved');
      return;
    }

    this.selectState.set('saving');
    this.api.select(choice).subscribe({
      next: (selected) => {
        this.characters.update((value) => (value ? { ...value, selected } : value));
        this.picked.set(selected);
        this.selectState.set('saved');
      },
      error: (error: unknown) => {
        this.selectError.set(this.errorMessage(error));
        this.selectState.set('error');
      },
    });
  }

  protected imageFailed(tokenId: string): void {
    this.brokenImages.update((broken) => new Set(broken).add(tokenId));
  }

  private errorMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 400) {
        const errors = (error.error?.errors ?? {}) as Record<string, string[]>;
        const first = Object.values(errors).flat()[0];
        if (first) {
          return first;
        }
      }
      if (error.status === 503) {
        return 'NFT ownership cannot be checked right now. Try again shortly.';
      }
      if (error.status === 429) {
        return 'Too many changes in a short time. Wait a minute and try again.';
      }
    }
    return 'Your choice was not saved. Try again.';
  }
}

function sameCharacter(a: SelectedCharacter | null | undefined, b: SelectedCharacter): boolean {
  return a?.kind === b.kind && a.id === b.id;
}
