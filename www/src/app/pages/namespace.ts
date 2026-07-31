// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input, resource } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GRAPH } from '../core/taxonomy-data';
import { KindBadge } from '../shared/kind-badge';

@Component({
  selector: 'docs-namespace',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, KindBadge],
  template: `
    <div class="space-y-6">
      <header class="space-y-1">
        <p class="text-foreground-subtle text-sm">Namespace</p>
        <h1 class="text-foreground font-mono text-2xl font-semibold tracking-tight">{{ name() }}</h1>
        <p class="text-foreground-muted text-sm">{{ rows().length }} types</p>
      </header>

      <ul class="divide-border border-border divide-y rounded-lg border">
        @for (row of rows(); track row.id) {
          <li class="flex flex-wrap items-baseline gap-3 px-4 py-3">
            <a [routerLink]="['/docs/api', row.slug]" class="text-foreground hover:text-primary font-medium transition-colors">
              {{ row.name }}
            </a>
            <docs-kind-badge [kind]="row.kind" />
          </li>
        }
      </ul>
    </div>
  `
})
export class NamespacePage {
  /** Bound from the route parameter by `withComponentInputBinding()`. */
  readonly namespace = input<string>('');

  private readonly nodes = resource({
    loader: async () => (await import('../../generated/nodes')).NODES
  });

  protected readonly name = computed(
    () => GRAPH.namespaces.find(entry => entry.slug === this.namespace())?.name ?? this.namespace()
  );

  protected readonly rows = computed(() =>
    (this.nodes.value() ?? [])
      .filter(node => node.slug.startsWith(`${this.namespace()}/`))
      .sort((left, right) => left.name.localeCompare(right.name))
  );
}
