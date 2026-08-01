// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { XuiToc, type XuiTocEntry } from '@xui/toc';
import { slugOf, type DocNode } from '../core/model';
import { PageMeta } from '../core/page-meta';
import { Breadcrumbs } from '../shared/breadcrumbs';
import { KindBadge } from '../shared/kind-badge';
import { Signature } from '../shared/signature';

/** The member tables, in the order a reader asks the questions. */
const MEMBER_GROUPS: { id: string; title: string; kinds: string[] }[] = [
  { id: 'fields', title: 'Fields and properties', kinds: ['field', 'constant', 'property', 'indexer'] },
  { id: 'events', title: 'Events', kinds: ['event'] },
  { id: 'methods', title: 'Methods', kinds: ['constructor', 'method', 'operator'] }
];

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
  imports: [RouterLink, Breadcrumbs, KindBadge, Signature, XuiToc],
  template: `
    @if (node(); as symbol) {
      <div class="flex gap-8">
      <article class="min-w-0 flex-1 space-y-8">
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

          <!-- The declaration gets the full block: it is the one line on the page a reader copies. -->
          <docs-signature [block]="true" [spans]="symbol.Signature" />

          @if (symbol.Summary) {
            <p class="text-foreground-muted leading-relaxed">{{ symbol.Summary }}</p>
          }

          @if (symbol.Docs) {
            <p class="text-sm">
              <a [routerLink]="['/docs/guide', ...symbol.Docs.split('/')]" class="text-primary hover:underline">
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
          <section id="as-a-kind" class="border-border rounded-lg border">
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
                      <a [routerLink]="['/docs/api', ...link.slug.split('/')]" class="text-primary font-mono text-xs hover:underline">
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
          <section id="remarks" class="space-y-2">
            <h2 class="text-foreground text-lg font-semibold">Remarks</h2>
            @for (paragraph of symbol.Remarks.split('\n\n'); track $index) {
              <p class="text-foreground-muted leading-relaxed">{{ paragraph }}</p>
            }
          </section>
        }

        <!-- Three tables rather than one list: a reader looking for a method is not reading past
             sixty properties to find it, and the kinds answer different questions — what a type
             holds, what it announces, what it does. -->
        @for (group of groups(); track group.id) {
          <section [id]="group.id" class="space-y-3">
            <h2 class="text-foreground text-lg font-semibold">
              {{ group.title }}
              <span class="text-foreground-subtle text-sm font-normal">({{ group.members.length }})</span>
            </h2>
            <ul class="divide-border border-border divide-y rounded-lg border">
              @for (member of group.members; track member.Id) {
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
          <section id="used-by" class="space-y-3">
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

      <!-- X3, and the reason it was specified: these pages run to a hundred members, and an outline
           that does not follow the reader down one is an outline nobody uses. -->
      <div class="hidden xl:block">
        <xui-toc class="w-56 shrink-0" label="On this page" [entries]="outline()" [basePath]="path()" scrollSpy />
      </div>
      </div>
    } @else {
      <p class="text-foreground-muted">No symbol at this address.</p>
    }
  `
})
export class SymbolPage {
  /** Bound from the resolver by `withComponentInputBinding()`. */
  readonly node = input<DocNode | undefined>();

  /**
   * The sections this page actually rendered — a facet panel, remarks, members, users — rather than
   * a fixed list, because most symbols have two of the four and an outline naming empty sections is
   * worse than none.
   */
  protected readonly outline = computed<XuiTocEntry[]>(() => {
    const symbol = this.node();

    if (!symbol) {
      return [];
    }

    const entries: XuiTocEntry[] = [];

    if (symbol.Facets) {
      entries.push({ id: 'as-a-kind', label: `As a ${symbol.Kind.replaceAll('-', ' ')}`, level: 2 });
    }

    if (symbol.Remarks) {
      entries.push({ id: 'remarks', label: 'Remarks', level: 2 });
    }

    for (const group of this.groups()) {
      entries.push({ id: group.id, label: `${group.title} (${group.members.length})`, level: 2 });
    }

    if (symbol.UsedBy?.length) {
      entries.push({ id: 'used-by', label: 'Used by', level: 2 });
    }

    return entries;
  });

  protected readonly path = computed(() => `/docs/api/${this.node()?.Slug ?? ''}`);

  /**
   * The members, split by what they are.
   *
   * A field and a property are the same question — what does this type hold — so they share a
   * table; an event is what it announces; a method, a constructor and an operator are what it does.
   * Empty groups are dropped rather than shown empty, which is why this is computed rather than
   * three `@if`s.
   */
  protected readonly groups = computed(() => {
    const members = this.node()?.Members ?? [];

    return MEMBER_GROUPS.map(group => ({
      ...group,
      members: members.filter(member => group.kinds.includes(member.MemberKind))
    })).filter(group => group.members.length > 0);
  });

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
  private readonly meta = inject(PageMeta);

  constructor() {
    // Written from the graph rather than from a template: a symbol's own summary is the sentence a
    // search result should show, and the fallback says what the page holds rather than repeating the
    // title into the void.
    effect(() => {
      const symbol = this.node();

      if (symbol) {
        this.meta.set(
          symbol.Summary ??
            `${symbol.QualifiedName} — the ${symbol.Kind.replaceAll('-', ' ')} in ${symbol.Assembly}, ` +
              `with its signature, members and what uses it.`,
          { title: `${symbol.Name} — Vixen` }
        );
      }
    });
  }

}
