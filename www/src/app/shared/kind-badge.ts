// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { XuiTag } from '@xui/tag';

/**
 * What a thing *is*, said in one word — docs/plan/25 § 2.3.
 *
 * The whole argument of the plan is that "component" and "system" are facts about the code rather
 * than labels somebody maintains, so this is not decoration: it is the first thing a reader looks at
 * and what a taxonomy page filters on.
 *
 * `minimal`, because these appear in bulk — which is exactly what `@xui/tag` documents the variant
 * for.
 */
@Component({
  selector: 'docs-kind-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiTag],
  template: `<xui-tag [color]="colour()" minimal>{{ label() }}</xui-tag>`
})
export class KindBadge {
  readonly kind = input.required<string>();

  protected readonly label = computed(() => this.kind().replaceAll('-', ' '));

  /** xUI's tag has five intents; the taxonomy has fifteen kinds, so they group by what they are. */
  protected readonly colour = computed<'none' | 'primary' | 'success' | 'warning' | 'error'>(() => {
    switch (this.kind()) {
      case 'component':
      case 'scene-component':
      case 'replicated-component':
        return 'success';
      case 'system':
      case 'behavior':
        return 'primary';
      case 'ui-control':
      case 'graph-node':
        return 'warning';
      case 'diagnostic':
      case 'log-event':
        return 'error';
      default:
        return 'none';
    }
  });
}
