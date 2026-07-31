// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * Derived by walking the graph's `declares` edge upward — docs/plan/25 § 8.4 — rather than stored:
 * area, then namespace, then the type. A trail kept beside a page is a second thing that can
 * disagree with the first.
 *
 * ⚠ Uses `@xui/breadcrumb` once X5's overflow collapse lands; six-level API trails are exactly the
 * case that needs it, and until then this is the plain list.
 */
@Component({
  selector: 'docs-breadcrumbs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <nav aria-label="Breadcrumb" class="text-foreground-muted flex flex-wrap items-center gap-1 text-sm">
      @for (crumb of crumbs(); track crumb.url; let last = $last) {
        @if (last) {
          <span class="text-foreground" aria-current="page">{{ crumb.label }}</span>
        } @else {
          <a [routerLink]="crumb.url" class="hover:text-foreground transition-colors">{{ crumb.label }}</a>
          <span aria-hidden="true" class="text-foreground-subtle">/</span>
        }
      }
    </nav>
  `
})
export class Breadcrumbs {
  readonly area = input<string | null>(null);
  readonly namespace = input<string | null>(null);
  readonly namespaceSlug = input<string | null>(null);
  readonly leaf = input.required<string>();

  protected readonly crumbs = computed(() => {
    const crumbs = [{ label: 'Docs', url: '/docs' }, { label: 'API', url: '/docs/api' }];
    const namespace = this.namespace();
    const slug = this.namespaceSlug();

    if (namespace && slug) {
      crumbs.push({ label: namespace, url: `/docs/api/${slug}` });
    }

    crumbs.push({ label: this.leaf(), url: '' });

    return crumbs;
  });
}
