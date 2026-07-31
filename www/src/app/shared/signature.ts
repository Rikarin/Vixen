// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { XuiCodeBlock, codeBlockTokenClasses, type XuiCodeLine, type XuiCodeTokenKind } from '@xui/code-block';
import type { DocSpan } from '../core/model';

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
  imports: [XuiCodeBlock],
  host: { class: 'block' },
  template: `
    @if (block()) {
      <xui-code-block size="sm" [code]="text()" [tokens]="lines()" [language]="language()" />
    } @else {
      <code class="font-mono text-sm leading-relaxed"
        >@for (span of spans(); track $index) {<span [class]="classOf(span[1])">{{ span[0] }}</span>}</code
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
    this.spans().map(([text, kind]) => ({ text, kind: kindOf(kind) }))
  ]);

  protected readonly text = computed(() => this.spans().map(([text]) => text).join(''));

  protected classOf(kind: string): string {
    return codeBlockTokenClasses[kindOf(kind)];
  }
}

/**
 * Roslyn's classification, in xUI's vocabulary.
 *
 * ⚠ **The two vocabularies are not the same size and that is the point.** Roslyn distinguishes a
 * struct from a class from an interface; a palette that coloured them differently would be saying
 * something a reader cannot use. Seventeen kinds cover both sides, so several of Roslyn's map onto
 * one — and the mapping is here, in the consumer, rather than in the generator, which should keep
 * emitting what the compiler actually said.
 */
function kindOf(kind: string): XuiCodeTokenKind {
  switch (kind) {
    case 'keyword':
      return 'keyword';
    case 'class':
    case 'struct':
    case 'interface':
    case 'enum':
    case 'delegate':
    case 'type-parameter':
      return 'type';
    case 'method':
      return 'function';
    case 'parameter':
      return 'variable';
    case 'property':
    case 'field':
    case 'event':
      return 'property';
    case 'number':
      return 'number';
    case 'string':
      return 'string';
    case 'operator':
      return 'operator';
    case 'punctuation':
      return 'punctuation';
    case 'namespace':
      return 'namespace';
    default:
      return 'plain';
  }
}
