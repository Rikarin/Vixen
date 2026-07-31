// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input, resource, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GRAPH, TAXONOMY } from '../core/taxonomy-data';
import type { NodeSummary } from '../core/model';

/**
 * "What can this engine do" — docs/plan/25 § 8.2.
 *
 * One filter over the graph per kind, and the answer to the row the plan opens with: a reader who has
 * installed the engine cannot tell which forty of three and a half thousand types they were meant to
 * reach for. These pages are that list.
 */
@Component({
  selector: 'docs-taxonomy',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    @if (entry(); as taxonomy) {
      <div class="space-y-6">
        <header class="space-y-2">
          <h1 class="text-foreground text-2xl font-semibold tracking-tight">{{ taxonomy.title }}</h1>
          <p class="text-foreground-muted">{{ taxonomy.blurb }}</p>
          <p class="text-foreground-subtle text-sm">
            {{ rows().length }} of {{ total }} documented types — classified from the code, not from a list.
          </p>
        </header>

        <input
          type="search"
          [value]="query()"
          (input)="onQuery($event)"
          placeholder="Filter by name or namespace"
          class="border-border bg-surface text-foreground placeholder:text-foreground-subtle w-full max-w-md rounded-lg border px-3 py-2 text-sm outline-none focus:border-primary"
        />

        <ul class="divide-border border-border divide-y rounded-lg border">
          @for (row of rows(); track row.id) {
            <li class="px-4 py-3">
              <a [routerLink]="['/docs/api', row.slug]" class="group flex flex-wrap items-baseline gap-2">
                <span class="text-foreground group-hover:text-primary font-medium transition-colors">{{ row.name }}</span>
                <span class="text-foreground-subtle font-mono text-xs">{{ row.namespace }}</span>
                @if (row.usedBy > 0) {
                  <span class="text-foreground-subtle ms-auto text-xs">used by {{ row.usedBy }}</span>
                }
              </a>
            </li>
          } @empty {
            <li class="text-foreground-muted px-4 py-8 text-center text-sm">
              @if (rows().length === 0 && query().length > 0) { Nothing matches that. } @else { Loading… }
            </li>
          }
        </ul>
      </div>
    }
  `
})
export class Taxonomy {
  /** Bound from the route's `data` by `withComponentInputBinding()`. */
  readonly taxonomy = input<string>('');

  protected readonly total = GRAPH.total;
  protected readonly query = signal('');

  protected readonly entry = computed(() => TAXONOMY.find(candidate => candidate.slug === this.taxonomy()));

  // The node list is a list page's own cost — 924 kB that the nav, the header and every symbol page
  // manage without.
  private readonly nodes = resource({
    loader: async () => (await import('../../generated/nodes')).NODES
  });

  protected readonly rows = computed<NodeSummary[]>(() => {
    const kind = this.entry()?.kind;
    const term = this.query().trim().toLowerCase();

    return (this.nodes.value() ?? [])
      .filter(node => node.kind === kind)
      .filter(node => term.length === 0 || node.qualifiedName.toLowerCase().includes(term))
      .sort((left, right) => left.name.localeCompare(right.name));
  });

  protected onQuery(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }
}
