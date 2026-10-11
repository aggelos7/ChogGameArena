import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface NftTrait {
  type: string;
  value: string;
}

/** Mirrors `NftCharacterResponse`: an NFT the server verified the signed-in wallet owns. */
export interface NftCharacter {
  /** uint256 in canonical decimal form; never a JavaScript number. */
  tokenId: string;
  name: string;
  /** Null when the metadata has no usable image. */
  imageUrl: string | null;
  traits: NftTrait[];
}

/** Mirrors `StandardCharacterResponse`. Drawn from its colour until artwork is approved (DEC-005). */
export interface StandardCharacter {
  id: string;
  name: string;
  color: string;
}

export type CharacterKind = 'Standard' | 'Nft';

export interface SelectedCharacter {
  kind: CharacterKind;
  id: string;
}

/** `NoWallet`: a guest. `Unavailable`: ownership could not be read right now. */
export type NftGalleryStatus = 'Available' | 'NoWallet' | 'Unavailable';

/** Mirrors `CharactersResponse` from `GET /api/characters`. */
export interface Characters {
  nftStatus: NftGalleryStatus;
  nfts: NftCharacter[];
  standard: StandardCharacter[];
  selected: SelectedCharacter;
}

/** Always the signed-in player's own characters; the server decides which NFTs are owned. */
@Injectable({ providedIn: 'root' })
export class CharactersApi {
  private readonly http = inject(HttpClient);

  get(): Observable<Characters> {
    return this.http.get<Characters>('/api/characters');
  }

  select(selection: SelectedCharacter): Observable<SelectedCharacter> {
    return this.http.put<SelectedCharacter>('/api/characters/selected', selection);
  }
}
