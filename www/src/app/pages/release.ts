// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CHANGE_SECTIONS, slugOf, type Change, type ReleaseDetail } from '../core/model';

/**
 * One release's table — docs/plan/25 § 6.2.
 *
 * Rendered from the JSON committed beside the release's archived graph, never recomputed here: a
 * table that could be rebuilt is a table that could come out different from the one that was
 * published, and the published one is what people upgraded against.
 *
 * The order of the sections is the order a reader needs them in — what breaks first, what is new
 * last — which is not the order they are interesting in.
 */
@Component({
  selector: 'docs-release',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    @if (release(); as detail) {
      <div class="space-y-8">
        <header class="space-y-2">
          <p class="text-foreground-subtle text-sm">
            <a routerLink="/docs/releases" class="hover:text-foreground transition-colors">Releases</a>
            · {{ detail.Release.Date }}
          </p>
          <h1 class="text-foreground text-2xl font-semibold tracking-tight">{{ detail.Release.Version }}</h1>
          <p class="text-foreground-muted">
            {{ detail.Release.Types }} types and {{ detail.Release.Members }} members.
            @if (detail.Previous) {
              Compared with
              <a [routerLink]="['/docs/releases', detail.Previous]" class="text-primary hover:underline">
                {{ detail.Previous }}</a
              >.
            } @else {
              The first release — there is nothing before it to compare against.
            }
          </p>
          @if (breaking() > 0) {
            <p class="text-danger text-sm font-medium">
              {{ breaking() }} breaking {{ breaking() === 1 ? 'change' : 'changes' }} — read those sections before
              upgrading.
            </p>
          }
        </header>

        @for (section of sections(); track section.kind) {
          <section class="space-y-3">
            <div>
              <h2 class="text-foreground text-lg font-semibold">{{ section.title }} ({{ section.rows.length }})</h2>
              <p class="text-foreground-muted text-sm">{{ section.blurb }}</p>
            </div>

            <ul class="divide-border border-border divide-y rounded-lg border">
              @for (row of section.rows; track row.Id) {
                <li class="space-y-1 px-4 py-3">
                  <div class="flex flex-wrap items-baseline gap-2">
                    <span class="text-foreground-subtle font-mono text-[0.7rem] tracking-wide uppercase">
                      {{ row.Taxonomy }}
                    </span>
                    @if (link(row); as slug) {
                      <a [routerLink]="['/docs/api', slug]" class="text-foreground hover:text-primary font-mono text-sm transition-colors">
                        {{ row.Display }}
                      </a>
                    } @else {
                      <span class="text-foreground font-mono text-sm">{{ row.Display }}</span>
                    }
                  </div>
                  @if (row.Note) {
                    <p class="text-foreground-muted text-sm">{{ row.Note }}</p>
                  }
                  @if (row.Before && row.After) {
                    <div class="text-foreground-subtle space-y-0.5 font-mono text-xs">
                      <p class="line-through">{{ row.Before }}</p>
                      <p class="text-foreground-muted">{{ row.After }}</p>
                    </div>
                  } @else if (row.After && section.kind === 'added') {
                    <p class="text-foreground-subtle font-mono text-xs">{{ row.After }}</p>
                  }
                </li>
              }
            </ul>
          </section>
        } @empty {
          <p class="text-foreground-muted text-sm">
            @if (release()?.Previous) {
              No public API changed in this release.
            } @else {
              The surface begins here, so there is nothing to list as a change.
            }
          </p>
        }
      </div>
    } @else {
      <p class="text-foreground-muted">No release at this address.</p>
    }
  `
})
export class ReleasePage {
  /** Bound from the resolver by `withComponentInputBinding()`. */
  readonly release = input<ReleaseDetail | undefined>();

  protected readonly breaking = computed(
    () => (this.release()?.Changes ?? []).filter(change => change.Kind !== 'added' && change.Kind !== 'deprecated').length
  );

  protected readonly sections = computed(() =>
    CHANGE_SECTIONS.map(section => ({
      ...section,
      rows: (this.release()?.Changes ?? []).filter(change => change.Kind === section.kind)
    })).filter(section => section.rows.length > 0)
  );

  /**
   * A row links to its symbol when the symbol still exists — so a removal, which is the row a reader
   * most wants to click, deliberately does not: there is no page left to send them to.
   */
  protected link(change: Change): string | null {
    return change.Kind === 'removed' || !change.Id.startsWith('T:') ? null : slugOf(change.Id);
  }
}
