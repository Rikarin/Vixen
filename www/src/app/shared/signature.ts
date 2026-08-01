// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { XuiCodeBlock, codeBlockTokenClasses, type XuiCodeLine } from '@xui/code-block';
import { tokenKind } from '../core/code';
import { slugOf, type DocSpan } from '../core/model';

/**
 * A signature, rendered from runs the generator already classified — docs/plan/25 § 3.4.
 *
 * There is no highlighter on this site. The spans arrive as `["public", "keyword"]` from Roslyn's own
 * `ToDisplayParts`, and `@xui/code-block` renders one text node per run against the theme's palette
 * — so the prerendered HTML is coloured for a reader with JavaScript off, and no grammar ships to
 * the browser. X1 exists for this input.
 */
@Component({
  selector: 'docs-signature',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiCodeBlock, RouterLink],
  host: { class: 'block' },
  template: `
    @if (block()) {
      <xui-code-block size="sm" [code]="text()" [tokens]="lines()" [language]="language()" />
    } @else {
      <code class="font-mono text-sm leading-relaxed"
        >@for (span of spans(); track $index) {@if (span[2]) {<a
            [routerLink]="['/docs/api', ...slug(span[2]!).split('/')]"
            [class]="classOf(span[1])"
            class="hover:decoration-primary underline decoration-transparent underline-offset-2 transition-colors"
            >{{ span[0] }}</a
          >} @else {<span [class]="classOf(span[1])">{{ span[0] }}</span>}}</code
      >
    }
  `
})
export class Signature {
  readonly spans = input.required<DocSpan[]>();
  readonly language = input<string | null>('csharp');

  /**
   * Whether this is the page's declaration or one of its members.
   *
   * ⚠ **A hundred members are a hundred figures**, and measured that way a symbol page went from
   * 16 kB to 104 kB — a copy button, a header and a gutter per line of a one-line signature. The
   * declaration at the top of the page is worth all of that; a member row is worth the palette and
   * nothing else, which is why `codeBlockTokenClasses` is exported and used directly here. One
   * palette either way, so the two never drift apart.
   */
  readonly block = input(false);

  /** One line: a declaration is one, and a member's is too. */
  protected readonly lines = computed<XuiCodeLine[]>(() => [
    this.spans().map(([text, kind]) => ({ text, kind: tokenKind(kind) }))
  ]);

  protected readonly text = computed(() => this.spans().map(([text]) => text).join(''));

  /** `T:Vixen.Ecs.World` → `vixen.ecs/world`, the same derivation the generator does. */
  protected slug(id: string): string {
    return slugOf(id);
  }

  protected classOf(kind: string): string {
    return codeBlockTokenClasses[tokenKind(kind)];
  }
}
