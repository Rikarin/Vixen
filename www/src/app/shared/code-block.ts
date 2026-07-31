// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * ⚠ Stand-in for `@xui/code-block` — docs/plan/25 § Part 9, X1. See ./README.md.
 *
 * The input that matters is `tokens`: this site's code arrives already classified, because the
 * highlighter is Roslyn rather than a grammar in the browser (§ 3.4). X1 exists so the package can
 * take those runs; until then, plain text with a filename header.
 */
@Component({
  selector: 'docs-code-block',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <figure class="border-border bg-surface overflow-hidden rounded-lg border">
      @if (filename()) {
        <figcaption
          class="border-border text-foreground-muted border-b px-4 py-2 font-mono text-xs"
        >
          {{ filename() }}
        </figcaption>
      }
      <pre class="overflow-x-auto p-4 text-sm leading-relaxed"><code>{{ code() }}</code></pre>
    </figure>
  `
})
export class CodeBlock {
  readonly code = input.required<string>();
  readonly language = input<string>('');
  readonly filename = input<string>('');
}
