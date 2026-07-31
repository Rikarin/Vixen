// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { XuiCodeBlock, type XuiCodeLine } from '@xui/code-block';
import { tokenKind } from '../core/code';
import { XuiProse } from '@xui/prose';
import type { DocSpan } from '../core/model';

/** One piece of a rendered page: a run of markdown, or a fence. */
interface Block {
  kind: 'markdown' | 'code';
  text: string;
  language: string;
  filename: string;
  tokens: XuiCodeLine[] | null;
}

/**
 * A guide page's body — docs/plan/25 § 4.
 *
 * The markdown arrives with its snippets already resolved (§ P2) and is rendered here; the *styling*
 * is `@xui/prose` (X2), which is why this emits bare `<h2>`, `<p>` and `<ul>` with no classes on
 * them. A markdown renderer is not a UI component and xUI does not ship one — but typography that
 * follows the theme's tokens in both themes is, and that half is now the package's.
 *
 * Fences go to `@xui/code-block` (X1) with whatever classification the generator produced for them,
 * so an example is coloured by the compiler that checked it rather than by a grammar in the browser.
 */
@Component({
  selector: 'docs-prose',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiCodeBlock, XuiProse],
  host: { class: 'block' },
  template: `
    <article xuiProse>
      @for (block of blocks(); track $index) {
        @if (block.kind === 'code') {
          <xui-code-block
            class="my-6"
            [code]="block.text"
            [tokens]="block.tokens"
            [language]="block.language"
            [filename]="block.filename"
          />
        } @else {
          <div [innerHTML]="block.text"></div>
        }
      }
    </article>
  `
})
export class Prose {
  readonly markdown = input.required<string>();

  /**
   * The page's own path, so a heading's self-link points down the page rather than at the site root.
   *
   * An Angular application ships `<base href="/">`, against which a bare `#id` resolves to `/#id` —
   * the failure `@xui/prose`'s anchor documents, and the reason this is threaded through rather than
   * assumed.
   */
  readonly basePath = input('');

  /**
   * Fences the generator classified, keyed by their order in the body.
   *
   * Absent for a language the build has no lexer for, which is the honest state: the block renders
   * as text rather than being guessed at.
   */
  readonly tokens = input<Record<string, DocSpan[][]> | undefined>(undefined);

  protected readonly blocks = computed<Block[]>(() => {
    const blocks: Block[] = [];
    const lines = this.markdown().replaceAll('\r\n', '\n').split('\n');
    const classified = this.tokens() ?? {};
    let buffer: string[] = [];
    let fence = 0;

    const flush = () => {
      if (buffer.length > 0) {
        blocks.push({
          kind: 'markdown',
          text: render(buffer.join('\n'), this.basePath()),
          language: '',
          filename: '',
          tokens: null
        });

        buffer = [];
      }
    };

    for (let index = 0; index < lines.length; index++) {
      if (!lines[index].startsWith('```')) {
        buffer.push(lines[index]);

        continue;
      }

      flush();

      const info = lines[index].slice(3).trim().split(' ');
      const filename = info.find(word => word.startsWith('title='))?.slice('title='.length) ?? '';
      const code: string[] = [];

      index++;

      while (index < lines.length && !lines[index].startsWith('```')) {
        code.push(lines[index]);
        index++;
      }

      const runs = classified[String(fence)];

      blocks.push({
        kind: 'code',
        text: code.join('\n'),
        language: info[0] ?? '',
        filename,
        tokens: runs ? runs.map(line => line.map(([text, kind]) => ({ text, kind: tokenKind(kind) }))) : null
      });

      fence++;
    }

    flush();

    return blocks;
  });
}

/**
 * The subset of markdown a guide page uses, and nothing else.
 *
 * No classes anywhere: `xuiProse` styles the elements, which is X2's whole point — the same markup
 * reads as one system with the rest of the site, in both themes, without this knowing a token name.
 */
function render(markdown: string, basePath: string): string {
  const escaped = markdown
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');

  return escaped
    .replace(/^### (.+)$/gm, (_, text) => heading(3, text, basePath))
    .replace(/^## (.+)$/gm, (_, text) => heading(2, text, basePath))
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2">$1</a>')
    .split(/\n{2,}/)
    .map(paragraph => {
      const trimmed = paragraph.trim();

      if (trimmed.length === 0 || trimmed.startsWith('<h')) {
        return trimmed;
      }

      if (trimmed.startsWith('- ')) {
        const items = trimmed
          .split('\n')
          .map(line => `<li>${line.replace(/^-\s+/, '')}</li>`)
          .join('');

        return `<ul>${items}</ul>`;
      }

      return `<p>${trimmed}</p>`;
    })
    .join('\n');
}

/**
 * A heading with an id and a self-link — § 8.5.
 *
 * The link is markup rather than `@xui/prose`'s anchor component, because this half of the page is
 * rendered through `innerHTML` and a component cannot be instantiated inside one. The href carries
 * the page's path for the reason that anchor documents: against `<base href="/">` a bare `#id` goes
 * to the site root.
 */
function heading(level: number, text: string, basePath: string): string {
  const id = text
    .toLowerCase()
    .replace(/[^\w\- ]/g, '')
    .replaceAll(' ', '-');

  return (
    `<h${level} id="${id}">${text}` +
    `<a class="xui-prose-anchor" href="${basePath}#${id}" aria-label="Link to this section">#</a>` +
    `</h${level}>`
  );
}
