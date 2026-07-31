// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { RELEASES } from '../../generated/releases';

/**
 * Every release, newest first — docs/plan/25 § 6.
 *
 * The list is the version store's own index: a release is a row here because its graph is committed
 * under `docs/api-history/`, not because somebody added it to a page.
 */
@Component({
  selector: 'docs-releases',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="space-y-6">
      <header class="space-y-2">
        <h1 class="text-foreground text-2xl font-semibold tracking-tight">Releases</h1>
        <p class="text-foreground-muted">
          What changed in each one, computed from the archived API graph rather than written by hand —
          so a breaking change is in the table whether or not anybody remembered it.
        </p>
      </header>

      @if (releases.length === 0) {
        <p class="text-foreground-muted text-sm">Nothing has been released yet.</p>
      } @else {
        <ul class="divide-border border-border divide-y rounded-lg border">
          @for (release of releases; track release.Version) {
            <li class="px-4 py-3">
              <a [routerLink]="['/docs/releases', release.Version]" class="group flex flex-wrap items-baseline gap-3">
                <span class="text-foreground group-hover:text-primary font-medium transition-colors">
                  {{ release.Version }}
                </span>
                <span class="text-foreground-subtle text-xs">{{ release.Date }}</span>
                <span class="text-foreground-muted text-xs">
                  {{ release.Types }} types · {{ release.Members }} members
                </span>
                @if (release.Breaking > 0) {
                  <span class="text-danger ms-auto text-xs font-medium">{{ release.Breaking }} breaking</span>
                } @else {
                  <span class="text-foreground-subtle ms-auto text-xs">no breaking changes</span>
                }
              </a>
            </li>
          }
        </ul>
      }
    </div>
  `
})
export class ReleasesPage {
  protected readonly releases = [...RELEASES].reverse();
}
