// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { slugOf, type GuidePage } from '../core/model';
import { Prose } from '../shared/prose';
import { TableOfContents } from '../shared/table-of-contents';

/**
 * A written page — docs/plan/25 § 4.
 *
 * The prose is the half nobody can generate; the signatures beside it are the half nobody should
 * write. The join is the page's `api:` list, which is also what put this page's link on those
 * symbols.
 */
@Component({
  selector: 'docs-guide',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, Prose, TableOfContents],
  template: `
    @if (page(); as guide) {
      <div class="flex gap-8">
        <article class="min-w-0 flex-1 space-y-6">
          <header class="space-y-2">
            <p class="text-foreground-subtle text-sm">{{ guide.Area }} · {{ guide.Kind }}</p>
            <h1 class="text-foreground text-2xl font-semibold tracking-tight">{{ guide.Title }}</h1>
            <p class="text-foreground-muted">{{ guide.Summary }}</p>
            @if (guide.Edit) {
              <a [href]="guide.Edit" rel="noreferrer" class="text-foreground-subtle hover:text-foreground text-xs transition-colors">
                Edit this page on GitHub
              </a>
            }
          </header>

          @if (symbols().length > 0) {
            <section class="border-border rounded-lg border p-4">
              <h2 class="text-foreground-muted mb-2 text-xs font-semibold tracking-wide uppercase">Documents</h2>
              <ul class="flex flex-wrap gap-2">
                @for (symbol of symbols(); track symbol.id) {
                  <li>
                    <a
                      [routerLink]="['/docs/api', symbol.slug]"
                      class="border-border hover:border-primary rounded border px-2 py-1 font-mono text-xs transition-colors"
                    >
                      {{ symbol.name }}
                    </a>
                  </li>
                }
              </ul>
            </section>
          }

          <docs-prose [markdown]="guide.Body" />

          @if (guide.Related.length > 0) {
            <footer class="border-border border-t pt-4">
              <h2 class="text-foreground-muted mb-2 text-xs font-semibold tracking-wide uppercase">Next</h2>
              <ul class="space-y-1">
                @for (slug of guide.Related; track slug) {
                  <li>
                    <a [routerLink]="['/docs/guide', slug]" class="text-primary text-sm hover:underline">{{ slug }}</a>
                  </li>
                }
              </ul>
            </footer>
          }
        </article>

        <div class="hidden xl:block">
          <docs-toc [entries]="guide.Headings" />
        </div>
      </div>
    } @else {
      <p class="text-foreground-muted">No page at this address.</p>
    }
  `
})
export class GuidePageComponent {
  /** Bound from the resolver by `withComponentInputBinding()`. */
  readonly page = input<GuidePage | undefined>();

  protected readonly symbols = computed(() =>
    (this.page()?.Api ?? []).map(id => {
      const qualified = id.replace(/^[A-Z]:/, '');

      return { id, name: qualified.slice(qualified.lastIndexOf('.') + 1), slug: slugOf(id) };
    })
  );
}
