// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GRAPH, GUIDE } from '../../generated/manifest';
import { TAXONOMY } from '../core/model';

/** "Everything Vixen offers" — the evaluator's page, docs/plan/25 § Part 0. */
@Component({
  selector: 'docs-overview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="space-y-10">
      <header class="space-y-3">
        <h1 class="text-foreground text-2xl font-semibold tracking-tight">What Vixen offers</h1>
        <p class="text-foreground-muted max-w-2xl leading-relaxed">
          A .NET game engine and application framework. Everything below is read from the engine's own
          source — a component is a component because the code says so, not because a list here does.
        </p>
      </header>

      <section class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        @for (entry of taxonomy; track entry.slug) {
          <a
            [routerLink]="['/docs', entry.slug]"
            class="border-border hover:border-primary group rounded-lg border p-4 transition-colors"
          >
            <div class="flex items-baseline justify-between gap-2">
              <h2 class="text-foreground group-hover:text-primary font-medium transition-colors">{{ entry.title }}</h2>
              <span class="text-foreground-subtle font-mono text-sm">{{ countOf(entry.kind) }}</span>
            </div>
            <p class="text-foreground-muted mt-1 text-sm">{{ entry.blurb }}</p>
          </a>
        }
      </section>

      @if (guide.length > 0) {
        <section class="space-y-3">
          <h2 class="text-foreground text-lg font-semibold">Guide</h2>
          <ul class="divide-border border-border divide-y rounded-lg border">
            @for (page of guide; track page.slug) {
              <li class="px-4 py-3">
                <a [routerLink]="['/docs/guide', page.slug]" class="text-foreground hover:text-primary font-medium transition-colors">
                  {{ page.title }}
                </a>
                <p class="text-foreground-muted mt-1 text-sm">{{ page.summary }}</p>
              </li>
            }
          </ul>
        </section>
      }

      <footer class="text-foreground-subtle border-border border-t pt-4 text-xs">
        {{ total }} types read from {{ graph.projects }} projects, built {{ graph.configuration }}@if (graph.commit) {
          <span> at {{ graph.commit.slice(0, 8) }}</span>
        }.
      </footer>
    </div>
  `
})
export class Overview {
  protected readonly graph = GRAPH;
  protected readonly guide = GUIDE;
  protected readonly taxonomy = TAXONOMY;
  protected readonly total = GRAPH.total;

  protected countOf(kind: string): number {
    return GRAPH.counts[kind] ?? 0;
  }
}
