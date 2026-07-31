// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { Injectable, inject, signal } from '@angular/core';
import { DOCUMENT, PLATFORM_ID } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { matchXRanges, type XMatchRange } from '@xui/core/query';

/** What the palette shows for one result. */
export interface SearchHit {
  url: string;
  label: string;
  context: string;
  kind: string;
  /** Which field matched — the strongest ranking signal FlexSearch hands back. */
  field: string;
  rank: number;
}

export interface SearchRequest {
  id: number;
  query: string;
  tags: string[];
  limit?: number;
}

export interface SearchResponse {
  id: number;
  hits: SearchHit[];
}

/**
 * Search — docs/plan/25 § Part 7.
 *
 * Three things happen here and the order matters. The **eager tier** (`search-names.ts`, 51 kB
 * Brotli) is imported when the palette first opens and answers name queries immediately. The
 * **worker** is started at the same moment and loads the full index behind it; from the first
 * answer it returns, its results replace the name-only ones. And if a browser has no worker — or
 * the index fails to load — the name tier keeps answering, so the palette degrades to "finds every
 * type by name" rather than to nothing.
 */
@Injectable({ providedIn: 'root' })
export class Search {
  private readonly platform = inject(PLATFORM_ID);
  private readonly document = inject(DOCUMENT);

  private worker: Worker | null = null;
  private names: [string, string, string, string, number][] | null = null;
  private sequence = 0;
  private readonly pending = new Map<number, (hits: SearchHit[]) => void>();

  /** True once the worker has answered at least once — the palette shows a spinner until then. */
  readonly full = signal(false);

  /** Starts the loading the palette needs. Called when it opens, not when the page does. */
  start(): void {
    if (!isPlatformBrowser(this.platform)) {
      return;
    }

    void this.loadNames();

    if (this.worker || typeof Worker === 'undefined') {
      return;
    }

    this.worker = new Worker(new URL('../search/search.worker', import.meta.url), { type: 'module' });

    this.worker.addEventListener('message', ({ data }: MessageEvent<SearchResponse>) => {
      const resolve = this.pending.get(data.id);

      if (resolve) {
        this.pending.delete(data.id);
        this.full.set(true);
        resolve(data.hits);
      }
    });
  }

  /** The omnibar's async provider — § Part 9, X4(a). */
  readonly query = async (query: string): Promise<SearchHit[]> => {
    const trimmed = query.trim();

    if (trimmed.length === 0) {
      return [];
    }

    this.start();

    if (!this.worker) {
      return this.byName(trimmed);
    }

    const id = ++this.sequence;

    const answered = new Promise<SearchHit[]>(resolve => {
      this.pending.set(id, resolve);
    });

    this.worker.postMessage({ id, query: trimmed, tags: this.tags(), limit: 30 } satisfies SearchRequest);

    // Whichever is ready first: the names are in memory, the worker is loading 1.5 MB. A reader who
    // types `World` and presses enter in 200 ms should not be waiting on the second one.
    return Promise.race([answered, this.byName(trimmed)]).then(hits => (hits.length > 0 ? hits : answered));
  };

  /** Set by the palette from its chips, read on the next query. */
  private selected: string[] = [];

  tags(): string[] {
    return this.selected;
  }

  setTags(tags: readonly string[]): void {
    this.selected = [...tags];
  }

  /** Where the match is in a label, for X4(e)'s `<mark>`s. */
  rangesOf(label: string, query: string): XMatchRange[] {
    return matchXRanges(label, query);
  }

  private async loadNames(): Promise<void> {
    this.names ??= (await import('../../generated/search-names')).NAMES as [
      string,
      string,
      string,
      string,
      number
    ][];
  }

  /** The eager tier: prefix and substring over 3 681 names, which is fast enough to do inline. */
  private async byName(query: string): Promise<SearchHit[]> {
    await this.loadNames();

    const lowered = query.toLowerCase();
    const kinds = this.selected.filter(tag => tag.startsWith('kind:')).map(tag => tag.slice(5));
    const hits: SearchHit[] = [];

    for (const [name, qualified, kind, slug, used] of this.names ?? []) {
      if (kinds.length > 0 && !kinds.includes(kind)) {
        continue;
      }

      const lower = name.toLowerCase();
      const at = lower.indexOf(lowered);

      if (at < 0 && !qualified.toLowerCase().includes(lowered)) {
        continue;
      }

      hits.push({
        url: kind === 'guide' ? `/docs/guide/${slug}` : `/docs/api/${slug}`,
        label: name,
        context: qualified.slice(0, Math.max(0, qualified.lastIndexOf('.'))),
        kind,
        field: at === 0 ? 'name' : 'qualifiedName',
        rank: used
      });
    }

    return hits
      .sort(
        (left, right) =>
          (right.label.toLowerCase() === lowered ? 1 : 0) - (left.label.toLowerCase() === lowered ? 1 : 0) ||
          right.rank - left.rank
      )
      .slice(0, 30);
  }

  /** The last places a reader went, for X4(g)'s empty-query list. */
  recent(): SearchHit[] {
    if (!isPlatformBrowser(this.platform)) {
      return [];
    }

    try {
      return JSON.parse(this.document.defaultView?.localStorage.getItem(RECENT) ?? '[]') as SearchHit[];
    } catch {
      return [];
    }
  }

  remember(hit: SearchHit): void {
    if (!isPlatformBrowser(this.platform)) {
      return;
    }

    const kept = [hit, ...this.recent().filter(other => other.url !== hit.url)].slice(0, 6);

    try {
      this.document.defaultView?.localStorage.setItem(RECENT, JSON.stringify(kept));
    } catch {
      // A browser with storage disabled loses the list and keeps the search, which is the right way
      // round.
    }
  }
}

const RECENT = 'vixen-docs-recent';
