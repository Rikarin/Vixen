// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { slugOf, type DocNode } from '../core/model';
import { Breadcrumbs } from '../shared/breadcrumbs';
import { KindBadge } from '../shared/kind-badge';
import { Signature } from '../shared/signature';

/**
 * One symbol — docs/plan/25 § 8.3.
 *
 * The kind panel is what makes this more than a signature dump: a component says what it costs per
 * chunk, a system says its phase and what it touches, a shader says its bindings. All of it is
 * derived (§ 2.6), and absent rather than guessed when it cannot be.
 */
@Component({
  selector: 'docs-symbol',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, Breadcrumbs, KindBadge, Signature],
  template: `
    @if (node(); as symbol) {
      <article class="min-w-0 space-y-8">
        <header class="space-y-3">
          <docs-breadcrumbs
            [namespace]="symbol.Namespace"
            [namespaceSlug]="namespaceSlug()"
            [leaf]="symbol.Name"
          />

          <div class="flex flex-wrap items-center gap-3">
            <h1 class="text-foreground text-2xl font-semibold tracking-tight">{{ symbol.Name }}</h1>
            <docs-kind-badge [kind]="symbol.Kind" />
            @if (symbol.Obsolete !== undefined) {
              <span class="text-error-emphasis text-xs font-medium">deprecated</span>
            }
            @if (symbol.Source?.Url) {
              <a
                [href]="symbol.Source!.Url"
                class="text-foreground-muted hover:text-foreground ms-auto font-mono text-xs transition-colors"
                rel="noreferrer"
              >
                {{ symbol.Source!.Path }}:{{ symbol.Source!.StartLine }}
              </a>
            }
          </div>

          <docs-signature
            class="border-border bg-surface overflow-x-auto rounded-lg border p-4"
            [spans]="symbol.Signature"
          />

          @if (symbol.Summary) {
            <p class="text-foreground-muted leading-relaxed">{{ symbol.Summary }}</p>
          }

          @if (symbol.Docs) {
            <p class="text-sm">
              <a [routerLink]="['/docs/guide', symbol.Docs]" class="text-primary hover:underline">
                Read the guide page for this →
              </a>
            </p>
          } @else {
            <p class="text-foreground-subtle text-sm">
              No guide page documents this yet — the page shows what the code says about itself.
            </p>
          }
        </header>

        <!-- The kind panel: § 2.3's whole argument, on the page. -->
        @if (symbol.Facets; as facets) {
          <section class="border-border rounded-lg border">
            <h2 class="border-border text-foreground border-b px-4 py-2 text-sm font-semibold">
              As a {{ symbol.Kind.replaceAll('-', ' ') }}
            </h2>
            <dl class="divide-border divide-y text-sm">
              @if (facets.SizeBytes !== undefined) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Size</dt>
                  <dd class="text-foreground">
                    {{ facets.SizeBytes }} bytes
                    @if (facets.EntitiesPerChunk !== undefined) {
                      <span class="text-foreground-subtle">
                        — {{ facets.EntitiesPerChunk }} rows in a 16 KB chunk, alone on the archetype
                      </span>
                    }
                  </dd>
                </div>
              }
              @if (facets.Phase) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Phase</dt>
                  <dd class="text-foreground font-mono">{{ facets.Phase }}</dd>
                </div>
              }
              @for (row of accessRows(); track row.label) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">{{ row.label }}</dt>
                  <dd class="flex flex-wrap gap-2">
                    @for (link of row.items; track link.id) {
                      <a [routerLink]="['/docs/api', link.slug]" class="text-primary font-mono text-xs hover:underline">
                        {{ link.name }}
                      </a>
                    }
                  </dd>
                </div>
              }
              @if (facets.Channel) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Replication</dt>
                  <dd class="text-foreground">
                    {{ facets.Channel }}, {{ facets.SendRate ? facets.SendRate + ' Hz' : 'every tick' }}
                  </dd>
                </div>
              }
              @for (quantized of facets.Quantized ?? []; track quantized.Field) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0 font-mono text-xs">{{ quantized.Field }}</dt>
                  <dd class="text-foreground-muted">
                    {{ quantized.Bits }} bits over [{{ quantized.Min }}, {{ quantized.Max }}]
                  </dd>
                </div>
              }
              @if (facets.Extensions) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Extensions</dt>
                  <dd class="text-foreground font-mono text-xs">{{ facets.Extensions.join(' ') }}</dd>
                </div>
              }
              @if (facets.MenuPath) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Create menu</dt>
                  <dd class="text-foreground font-mono text-xs">{{ facets.MenuPath }}</dd>
                </div>
              }
              @if (facets.Stages) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Stages</dt>
                  <dd class="text-foreground font-mono text-xs">{{ facets.Stages.join(', ') }}</dd>
                </div>
              }
              @if (facets.DescriptorSets !== undefined) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Bindings</dt>
                  <dd class="text-foreground">
                    {{ facets.DescriptorSets }} descriptor sets, {{ facets.ShaderParameters }} parameters
                  </dd>
                </div>
              }
              @if (facets.Permutations) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Permutations</dt>
                  <dd class="text-foreground-muted font-mono text-xs">{{ facets.Permutations.join(', ') }}</dd>
                </div>
              }
              @if (facets.Targets) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Valid on</dt>
                  <dd class="text-foreground">
                    {{ facets.Targets.join(', ') }}@if (facets.AllowMultiple) {<span>, repeatable</span>}
                  </dd>
                </div>
              }
              @if (facets.Level) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Level</dt>
                  <dd class="text-foreground">{{ facets.Level }}</dd>
                </div>
              }
              @if (facets.EmittedBy) {
                <div class="flex gap-4 px-4 py-2">
                  <dt class="text-foreground-muted w-48 shrink-0">Emitted by</dt>
                  <dd class="text-foreground font-mono text-xs">{{ facets.EmittedBy.join(', ') }}</dd>
                </div>
              }
            </dl>
          </section>
        }

        @if (symbol.Remarks) {
          <section class="space-y-2">
            <h2 class="text-foreground text-lg font-semibold">Remarks</h2>
            @for (paragraph of symbol.Remarks.split('\n\n'); track $index) {
              <p class="text-foreground-muted leading-relaxed">{{ paragraph }}</p>
            }
          </section>
        }

        @if (symbol.Members?.length) {
          <section class="space-y-3">
            <h2 class="text-foreground text-lg font-semibold">
              Members <span class="text-foreground-subtle text-sm font-normal">({{ symbol.Members!.length }})</span>
            </h2>
            <ul class="divide-border border-border divide-y rounded-lg border">
              @for (member of symbol.Members!; track member.Id) {
                <li class="space-y-1 px-4 py-3">
                  <docs-signature [spans]="member.Signature" />
                  @if (member.Summary) {
                    <p class="text-foreground-muted text-sm">{{ member.Summary }}</p>
                  }
                </li>
              }
            </ul>
          </section>
        }

        @if (symbol.UsedBy?.length) {
          <section class="space-y-3">
            <h2 class="text-foreground text-lg font-semibold">
              Used by
              <span class="text-foreground-subtle text-sm font-normal">
                ({{ symbol.UsedByCount }}@if (symbol.UsedByCount! > symbol.UsedBy!.length) {<span>, showing {{ symbol.UsedBy!.length }}</span>})
              </span>
            </h2>
            <!-- Samples first: a use in a sample is a worked example, a use in the engine is an
                 implementation detail — § 2.4. -->
            <ul class="flex flex-wrap gap-2">
              @for (reference of symbol.UsedBy!; track reference.Id) {
                <li
                  class="border-border rounded border px-2 py-1 text-xs"
                  [class.border-primary]="reference.Area === 'Samples'"
                >
                  <span class="text-foreground">{{ reference.Name }}</span>
                  <span class="text-foreground-subtle ms-1">{{ reference.Assembly }}</span>
                </li>
              }
            </ul>
          </section>
        }
      </article>
    } @else {
      <p class="text-foreground-muted">No symbol at this address.</p>
    }
  `
})
export class SymbolPage {
  /** Bound from the resolver by `withComponentInputBinding()`. */
  readonly node = input<DocNode | undefined>();

  protected readonly namespaceSlug = computed(() => {
    const slug = this.node()?.Slug ?? '';

    return slug.slice(0, slug.lastIndexOf('/'));
  });

  /** The system-access rows, with a link where the graph has the component and plain text where not. */
  protected readonly accessRows = computed(() => {
    const facets = this.node()?.Facets;

    if (!facets) {
      return [];
    }

    const rows: { label: string; items: { id: string; name: string; slug: string }[] }[] = [];
    const add = (label: string, ids: string[] | undefined) => {
      if (!ids?.length) {
        return;
      }

      // Derived rather than looked up: the URL of a symbol is a function of its id (§ 2.2), so a
      // link costs nothing and this page never loads the node list to make one.
      rows.push({
        label,
        items: ids.map(id => {
          const qualified = id.replace(/^T:/, '');

          return { id, name: qualified.slice(qualified.lastIndexOf('.') + 1), slug: slugOf(id) };
        })
      });
    };

    add('Reads', facets.Reads);
    add('Writes', facets.Writes);
    add('Runs before', facets.RunsBefore);
    add('Runs after', facets.RunsAfter);

    return rows;
  });
}
