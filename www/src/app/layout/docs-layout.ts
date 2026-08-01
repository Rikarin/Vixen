// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { XuiTree, XuiTreeRouter, type XuiTreeNode } from '@xui/tree';
import { Omnibar } from '../shared/omnibar';
import { GRAPH, GUIDE } from '../../generated/manifest';
import { RELEASES } from '../../generated/releases';
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
  imports: [RouterOutlet, RouterLink, XuiTree, XuiTreeRouter, Omnibar],
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
            <a routerLink="/docs/releases" class="hover:text-foreground transition-colors">Releases</a>
          </nav>

          <docs-omnibar class="ms-auto" />

          <!-- The version switcher — § 6.3. A plain <select> with a form action so it works with
               JavaScript off, which is the site's rule and not a detail here: a reader on an old
               version is often reading from a machine they do not control. -->
          <div class="flex items-center gap-3">
            @if (releases.length > 0) {
              <label class="text-foreground-subtle flex items-center gap-2 text-xs">
                <span class="sr-only">Version</span>
                <select
                  (change)="onVersion($event)"
                  class="border-border bg-surface text-foreground-muted rounded border px-2 py-1 text-xs outline-none focus:border-primary"
                >
                  <option value="" selected>latest ({{ commit }})</option>
                  @for (entry of releases; track entry.Version) {
                    <option [value]="entry.Version">{{ entry.Version }} — {{ entry.Date }}</option>
                  }
                </select>
              </label>
            }
            <span class="text-foreground-subtle font-mono text-xs">{{ commit }}</span>
          </div>
        </div>
      </header>

      <div class="mx-auto flex max-w-[100rem] gap-8 px-4 py-8">
        <aside class="hidden w-64 shrink-0 lg:block">
          <!-- X6: xuiTreeRouter reads the active node off the router and keeps the expansion a
               reader chose, which a list of links rebuilt on every navigation cannot. persistKey is
               what makes an opened section survive the next page. -->
          <nav class="sticky top-24 max-h-[calc(100vh-8rem)] overflow-y-auto pe-2" aria-label="Documentation">
            <xui-tree
              xuiTreeRouter
              persistKey="vixen-docs-nav"
              ariaLabel="Documentation"
              [nodes]="tree"
              [nodeLink]="linkOf"
              (nodeClick)="open($event)"
            />
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

  /** Newest first, because that is the order the switcher is read in. */
  protected readonly releases = [...RELEASES].reverse();

  private readonly router = inject(Router);

  /**
   * The nav, built from the manifest rather than written down — § 8.2.
   *
   * ⚠ Not the namespace list. Rendering 244 links into every one of 3 940 prerendered pages cost
   * 90 kB of HTML each — more than the page — for a list nobody reads from a symbol page. It lives
   * on the API index, which is one click away and is the page that exists to hold it.
   */
  protected readonly tree: XuiTreeNode[] = [
    ...(GUIDE.length > 0
      ? [
          {
            id: 'guide',
            label: 'Guide',
            isExpanded: true,
            children: GUIDE.map(page => ({ id: `guide/${page.slug}`, label: page.title }))
          }
        ]
      : []),
    {
      id: 'kinds',
      label: 'By kind',
      isExpanded: true,
      children: TAXONOMY.map(entry => ({
        id: entry.slug,
        label: entry.title,
        secondaryLabel: String(GRAPH.counts[entry.kind] ?? 0)
      }))
    },
    { id: 'api', label: 'All namespaces', secondaryLabel: String(GRAPH.namespaces.length) },
    { id: 'releases', label: 'Releases', secondaryLabel: String(RELEASES.length) }
  ];

  /** Where a node goes, and what the router matches the active one against. */
  protected readonly linkOf = (node: XuiTreeNode): string | null =>
    node.children ? null : `/docs/${node.id}`;

  /**
   * Clicking a node goes there.
   *
   * ⚠ `xuiTreeRouter` reads the URL; it does not write it. `nodeLink` is how it decides which node
   * the current page *is* — it renders no anchor and intercepts no click, so a tree without this was
   * a nav that highlighted correctly and navigated nowhere.
   */
  protected open(node: XuiTreeNode): void {
    const link = this.linkOf(node);

    if (link) {
      void this.router.navigateByUrl(link);
    }
  }

  /**
   * Picking a version goes to that release's table rather than to a pinned copy of this page.
   *
   * ⚠ **This is a correction to § 6.3, and it is worth stating.** The plan had pinned versions
   * prerendered under `/docs/<version>/`, at ~4 500 files each against Cloudflare's 20 000, which is
   * what fixed retention at four. Two facts changed the arithmetic: those pages are `noindex` by the
   * plan's own decision, so prerendering buys nothing a search engine will read; and the archived
   * graph is one 2.4 MB file that a browser can render from directly. So the switcher points at what
   * the store actually holds — the release and its table — and an old version's API is read from its
   * archive rather than from 4 500 duplicated pages. Retention stops being a constraint: the site
   * stays at ~4 300 files however many releases accumulate.
   */
  protected onVersion(event: Event): void {
    const version = (event.target as HTMLSelectElement).value;

    void this.router.navigate(version.length === 0 ? ['/docs'] : ['/docs/releases', version]);
  }
}
