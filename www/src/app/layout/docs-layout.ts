// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { GRAPH, GUIDE } from '../../generated/manifest';
import { TAXONOMY } from '../core/model';

/**
 * The documentation frame: the areas on the left, the page in the middle, and whatever outline the
 * page provides on the right.
 *
 * The nav is built from the manifest rather than written down — docs/plan/25 § 8.2 — so a new
 * namespace or a new guide page appears in it without anybody editing a list.
 */
@Component({
  selector: 'docs-layout',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="min-h-full">
      <header class="border-border bg-background/80 sticky top-0 z-20 border-b backdrop-blur">
        <div class="mx-auto flex max-w-[100rem] items-center gap-6 px-4 py-3">
          <a routerLink="/" class="text-foreground text-lg font-semibold tracking-tight">Vixen</a>
          <nav class="text-foreground-muted flex items-center gap-4 text-sm">
            <a routerLink="/docs" class="hover:text-foreground transition-colors">Overview</a>
            <a routerLink="/docs/api" class="hover:text-foreground transition-colors">API</a>
            <a routerLink="/docs/components" class="hover:text-foreground transition-colors">Components</a>
            <a routerLink="/docs/systems" class="hover:text-foreground transition-colors">Systems</a>
            <a routerLink="/docs/shaders" class="hover:text-foreground transition-colors">Shaders</a>
          </nav>
          <span class="text-foreground-subtle ms-auto font-mono text-xs">
            {{ commit }}
          </span>
        </div>
      </header>

      <div class="mx-auto flex max-w-[100rem] gap-8 px-4 py-8">
        <aside class="hidden w-64 shrink-0 lg:block">
          <nav class="sticky top-24 max-h-[calc(100vh-8rem)] space-y-6 overflow-y-auto pe-2">
            @if (guide.length > 0) {
              <div>
                <p class="text-foreground-subtle mb-2 text-xs font-semibold tracking-wide uppercase">Guide</p>
                <ul class="space-y-1">
                  @for (page of guide; track page.slug) {
                    <li>
                      <a
                        [routerLink]="['/docs/guide', page.slug]"
                        routerLinkActive="text-foreground font-medium"
                        class="text-foreground-muted hover:text-foreground block truncate text-sm transition-colors"
                      >
                        {{ page.title }}
                      </a>
                    </li>
                  }
                </ul>
              </div>
            }

            <div>
              <p class="text-foreground-subtle mb-2 text-xs font-semibold tracking-wide uppercase">By kind</p>
              <ul class="space-y-1">
                @for (entry of taxonomy; track entry.slug) {
                  <li>
                    <a
                      [routerLink]="['/docs', entry.slug]"
                      routerLinkActive="text-foreground font-medium"
                      class="text-foreground-muted hover:text-foreground flex items-center justify-between gap-2 text-sm transition-colors"
                    >
                      <span class="truncate">{{ entry.title }}</span>
                      <span class="text-foreground-subtle text-xs">{{ countOf(entry.kind) }}</span>
                    </a>
                  </li>
                }
              </ul>
            </div>

            <div>
              <!-- ⚠ Not the namespace list. Rendering 364 links into every one of 3 939 prerendered
                   pages cost 90 kB of HTML each — more than the page — for a list nobody reads from
                   a symbol page. It lives on the API index, which is one click away and is the page
                   that exists to hold it. -->
              <a
                routerLink="/docs/api"
                class="text-foreground-muted hover:text-foreground flex items-center justify-between gap-2 text-sm transition-colors"
              >
                <span>All namespaces</span>
                <span class="text-foreground-subtle text-xs">{{ namespaces.length }}</span>
              </a>
            </div>
          </nav>
        </aside>

        <main class="min-w-0 flex-1">
          <router-outlet />
        </main>
      </div>
    </div>
  `
})
export class DocsLayout {
  protected readonly guide = GUIDE;
  protected readonly taxonomy = TAXONOMY;
  protected readonly namespaces = GRAPH.namespaces;
  protected readonly commit = GRAPH.commit ? GRAPH.commit.slice(0, 8) : 'working tree';

  protected countOf(kind: string): number {
    return GRAPH.counts[kind] ?? 0;
  }
}
