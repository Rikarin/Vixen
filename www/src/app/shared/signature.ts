// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { DocSpan } from '../core/model';

/**
 * A signature, rendered from runs the generator already classified — docs/plan/25 § 3.4.
 *
 * There is no highlighter on this site. The spans arrive as `["public", "keyword"]` from Roslyn's own
 * `ToDisplayParts`, and this maps a kind to a class; the prerendered HTML is coloured for a reader
 * with JavaScript off, and no grammar ships to the browser.
 */
@Component({
  selector: 'docs-signature',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block font-mono text-sm leading-relaxed' },
  template: `<code>@for (span of spans(); track $index) {<span [class]="classOf(span[1])">{{ span[0] }}</span>}</code>`
})
export class Signature {
  readonly spans = input.required<DocSpan[]>();

  protected classOf(kind: string): string {
    switch (kind) {
      case 'keyword':
        return 'text-primary-emphasis';
      case 'class':
      case 'struct':
      case 'interface':
      case 'enum':
      case 'delegate':
        return 'text-info-emphasis';
      case 'type-parameter':
        return 'text-warning-emphasis';
      case 'method':
      case 'property':
      case 'field':
      case 'event':
        return 'text-success-emphasis';
      case 'number':
      case 'string':
        return 'text-secondary-emphasis';
      case 'punctuation':
      case 'operator':
        return 'text-foreground-muted';
      default:
        return 'text-foreground';
    }
  }
}
