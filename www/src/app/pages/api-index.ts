// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GRAPH } from '../../generated/manifest';

/** Areas → namespaces. Scoped by area rather than by packability — docs/plan/25 § 6.3. */
@Component({
  selector: 'docs-api-index',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="space-y-8">
      <header class="space-y-2">
        <h1 class="text-foreground text-2xl font-semibold tracking-tight">API</h1>
        <p class="text-foreground-muted">
          {{ total }} types in {{ namespaces }} namespaces, read from the source of
          {{ graph.projects }} projects.
        </p>
      </header>

      @for (area of areas(); track area.name) {
        <section class="space-y-3">
          <h2 class="text-foreground text-lg font-semibold">{{ area.name }}</h2>
          <ul class="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
            @for (entry of area.namespaces; track entry.slug) {
              <li>
                <a
                  [routerLink]="['/docs/api', ...entry.slug.split('/')]"
                  class="border-border hover:border-primary flex items-center justify-between gap-2 rounded-lg border px-3 py-2 text-sm transition-colors"
                >
                  <span class="text-foreground truncate font-mono text-xs">{{ entry.name }}</span>
                  <span class="text-foreground-subtle text-xs">{{ entry.count }}</span>
                </a>
              </li>
            }
          </ul>
        </section>
      }
    </div>
  `
})
export class ApiIndex {
  protected readonly graph = GRAPH;
  protected readonly total = GRAPH.total;
  protected readonly namespaces = GRAPH.namespaces.length;

  protected readonly areas = computed(() => {
    const byArea = new Map<string, typeof GRAPH.namespaces>();

    for (const entry of GRAPH.namespaces) {
      for (const area of entry.areas) {
        byArea.set(area, [...(byArea.get(area) ?? []), entry]);
      }
    }

    return [...byArea.entries()]
      .map(([name, namespaces]) => ({ name, namespaces: namespaces.sort((a, b) => a.name.localeCompare(b.name)) }))
      .sort((left, right) => left.name.localeCompare(right.name));
  });
}
