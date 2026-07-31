// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { XuiKbd } from '@xui/kbd';
import { XuiOmnibar, XuiOmnibarEmpty, type XuiOmnibarTag } from '@xui/omnibar';
import { GRAPH } from '../../generated/manifest';
import { TAXONOMY } from '../core/model';
import { Search, type SearchHit } from '../core/search';

/**
 * ⌘K over everything — docs/plan/25 § Part 7, on `@xui/omnibar` with X4's deltas.
 *
 * Every one of those deltas is load-bearing here rather than decorative: the provider is **async**
 * because the index is a worker away; results are **grouped** because a query hits three kinds of
 * thing at once and a flat list makes a reader read all of it; the chips are **tags** because
 * "systems only" is how you ask a 3 734-type engine a question; and the ranges are **marked**
 * because the whole point of a hit is seeing why it matched.
 */
@Component({
  selector: 'docs-omnibar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiOmnibar, XuiOmnibarEmpty, XuiKbd],
  template: `
    <button
      type="button"
      (click)="open.set(true)"
      class="border-border bg-surface text-foreground-subtle hover:border-primary flex items-center gap-2 rounded-lg border px-3 py-1.5 text-sm transition-colors"
    >
      <span>Search</span>
      <kbd xuiKbd size="sm">⌘K</kbd>
    </button>

    <xui-omnibar
      [(open)]="open"
      [hotkey]="hotkeys"
      placeholder="Search types, members and guides…"
      noResultsText="Nothing matches that."
      loadingText="Reading the index…"
      [itemsProvider]="provider"
      [itemText]="label"
      [itemGroup]="group"
      [tags]="chips"
      [(selectedTags)]="tags"
      [itemTags]="tagsOf"
      [itemRanges]="ranges"
      highlightMatches
      [recentItems]="recent()"
      recentLabel="Where you were"
      [debounce]="80"
      [virtualScrollThreshold]="200"
      (itemSelected)="go($event)"
    >
      <ng-template xuiOmnibarEmpty let-query>
        <p class="text-foreground-muted p-6 text-center text-sm">
          Nothing matches “{{ query }}”. Every public type is in here by name — a miss usually means
          the name is different from the one you expected, so try a word of it.
        </p>
      </ng-template>
    </xui-omnibar>
  `
})
export class Omnibar {
  private readonly search = inject(Search);
  private readonly router = inject(Router);

  protected readonly open = signal(false);

  /** ⌘K, and `/` the way every documentation site has taught readers to expect — § 8.5. */
  protected readonly hotkeys = ['mod+k', '/'];

  protected readonly provider = (query: string) => this.search.query(query);
  protected readonly label = (hit: SearchHit) => hit.label;
  protected readonly ranges = (hit: SearchHit, query: string) => this.search.rangesOf(hit.label, query);
  protected readonly tags = signal<readonly string[]>([]);

  /**
   * The group heading a hit falls under.
   *
   * By kind rather than by area, because kind is what the taxonomy sorts the engine by and what a
   * reader is actually distinguishing between: a component, a system, a page about either.
   */
  protected readonly group = (hit: SearchHit) => headingFor(hit.kind);

  protected readonly tagsOf = (hit: SearchHit) => [`kind:${hit.kind}`];

  /**
   * The chips: the taxonomy's kinds, plus the two that are not taxonomy kinds and are still what
   * people look for — a guide page, and a plain type. Ordered by how many of each the engine has,
   * so the chip that filters the most sits first.
   */
  protected readonly chips: XuiOmnibarTag[] = [
    { id: 'kind:guide', label: 'Guides' },
    ...TAXONOMY.map(entry => ({ id: `kind:${entry.kind}`, label: entry.title })),
    { id: 'kind:class', label: 'Classes' },
    { id: 'kind:struct', label: 'Structs' },
    { id: 'kind:interface', label: 'Interfaces' },
    { id: 'kind:method', label: 'Methods' },
    { id: 'kind:property', label: 'Properties' }
  ].filter(chip => chip.id === 'kind:guide' || (GRAPH.counts[chip.id.slice(5)] ?? 1) > 0);

  protected readonly recent = computed(() => this.search.recent());

  constructor() {
    // The chips reach the worker through the service on the next query rather than being pushed at
    // it, so a chip toggled while a query is in flight cannot answer the previous question.
    effect(() => this.search.setTags(this.tags()));

    // Loading starts when the palette opens, not when the page does: a reader who never searches
    // pays nothing for 1.5 MB of index.
    effect(() => {
      if (this.open()) {
        this.search.start();
      }
    });
  }

  protected go(hit: SearchHit): void {
    this.search.setTags(this.tags());
    this.search.remember(hit);

    const [path, fragment] = hit.url.split('#');

    void this.router.navigate([path], fragment ? { fragment } : {});
  }
}

function headingFor(kind: string): string {
  const known = TAXONOMY.find(entry => entry.kind === kind);

  if (known) {
    return known.title;
  }

  switch (kind) {
    case 'guide':
      return 'Guides';
    case 'method':
    case 'property':
    case 'field':
    case 'event':
    case 'constructor':
      return 'Members';
    default:
      return 'Types';
  }
}
