// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import type { XuiCodeTokenKind } from '@xui/code-block';

/**
 * Roslyn's classification, in xUI's vocabulary — docs/plan/25 § 3.4.
 *
 * ⚠ **The two vocabularies are not the same size and that is the point.** Roslyn distinguishes a
 * struct from a class from an interface; a palette that coloured them differently would be saying
 * something a reader cannot use. Seventeen kinds cover both sides, so several of Roslyn's map onto
 * one — and the mapping lives here, in the consumer, rather than in the generator, which should keep
 * emitting what the compiler actually said.
 *
 * The same runs arrive from two places and go through this once: a symbol's signature, synthesised
 * from `ToDisplayParts`, and a guide's code fence, classified from the tree the build compiled.
 */
export function tokenKind(kind: string): XuiCodeTokenKind {
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
    case 'local':
      return 'variable';
    case 'property':
    case 'field':
    case 'event':
      return 'property';
    case 'number':
      return 'number';
    case 'string':
      return 'string';
    case 'comment':
      return 'comment';
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
