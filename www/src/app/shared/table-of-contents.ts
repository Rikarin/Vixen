// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

/** One entry in the outline. Matches the generator's `Headings`. */
export interface TocEntry {
  Id: string;
  Text: string;
  Level: number;
}

/**
 * ⚠ Stand-in for `@xui/toc` — docs/plan/25 § Part 9, X3. See ./README.md.
 *
 * Links rather than scroll-spy: the router is configured with anchor scrolling, so a click lands on
 * the heading and the URL stays shareable. Scroll-spy is the delta X3 adds, and adding it here would
 * be the part thrown away.
 */
@Component({
  selector: 'docs-toc',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  host: { class: 'w-56 shrink-0' },
  template: `
    <nav class="sticky top-24 max-h-[calc(100vh-8rem)] overflow-y-auto" aria-label="On this page">
      <p class="text-foreground-subtle mb-2 text-xs font-semibold tracking-wide uppercase">On this page</p>
      <ul class="border-border space-y-1 border-s ps-3">
        @for (entry of entries(); track entry.Id) {
          <li [class]="entry.Level === 3 ? 'ps-3' : ''">
            <a
              [routerLink]="[]"
              [fragment]="entry.Id"
              class="text-foreground-muted hover:text-foreground block truncate text-sm transition-colors"
            >
              {{ entry.Text }}
            </a>
          </li>
        }
      </ul>
    </nav>
  `
})
export class TableOfContents {
  readonly entries = input.required<TocEntry[]>();
}
