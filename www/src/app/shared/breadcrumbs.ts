// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  XuiBreadcrumb,
  XuiBreadcrumbItem,
  XuiBreadcrumbLink,
  XuiBreadcrumbList,
  XuiBreadcrumbPage,
  XuiBreadcrumbSeparator,
  type XuiBreadcrumbData
} from '@xui/breadcrumb';

/**
 * Derived by walking the graph's `declares` edge upward — docs/plan/25 § 8.4 — rather than stored:
 * area, then namespace, then the type. A trail kept beside a page is a second thing that can
 * disagree with the first.
 *
 * ⚠ **The primitives, not `<xui-breadcrumbs>`, and the reason is a bug worth reporting rather than
 * a preference.** The collapsing variant — X5's delta, which these six-level trails are exactly the
 * case for — measures its items through `@xui/overflow-list`, and that measurement throws under
 * prerendering: `this.ruler.nativeElement.children is not iterable`, because the server DOM's
 * `children` is not an iterable `HTMLCollection`. The throw does not merely skip the trail; it
 * aborts the rest of the page, and the symbol pages came out with no breadcrumb, no outline and no
 * nav tree at all. Guarding the ruler with an `isPlatformBrowser` check (or iterating with
 * `Array.from`) fixes it in one line in the package; until then this composes the same package's
 * primitives, which render server-side exactly as they should.
 */
@Component({
  selector: 'docs-breadcrumbs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    XuiBreadcrumb,
    XuiBreadcrumbItem,
    XuiBreadcrumbLink,
    XuiBreadcrumbList,
    XuiBreadcrumbPage,
    XuiBreadcrumbSeparator
  ],
  template: `
    <nav xuiBreadcrumb>
      <ol xuiBreadcrumbList>
        @for (crumb of crumbs(); track crumb.text; let last = $last) {
          <!-- The item lays its own crumb and separator out: the list is a wrapping flex row, and a
               separator that inherits nothing from it lands on the next line. -->
          <li xuiBreadcrumbItem class="flex items-center gap-1.5">
            @if (last) {
              <span xuiBreadcrumbPage>{{ crumb.text }}</span>
            } @else {
              <a xuiBreadcrumbLink [routerLink]="crumb.link">{{ crumb.text }}</a>
              <span xuiBreadcrumbSeparator></span>
            }
          </li>
        }
      </ol>
    </nav>
  `
})
export class Breadcrumbs {
  readonly area = input<string | null>(null);
  readonly namespace = input<string | null>(null);
  readonly namespaceSlug = input<string | null>(null);
  readonly leaf = input.required<string>();

  protected readonly crumbs = computed<XuiBreadcrumbData[]>(() => {
    const crumbs: XuiBreadcrumbData[] = [
      { text: 'Docs', link: '/docs' },
      { text: 'API', link: '/docs/api' }
    ];

    const namespace = this.namespace();
    const slug = this.namespaceSlug();

    if (namespace && slug) {
      crumbs.push({ text: namespace, link: `/docs/api/${slug}` });
    }

    // No link, and current by position: the last crumb is the page the reader is already on.
    crumbs.push({ text: this.leaf() });

    return crumbs;
  });
}
