// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { XuiCodeBlock, type XuiCodeLine } from '@xui/code-block';
import { tokenKind } from '../core/code';
import { XuiProse, XuiProseAnchor } from '@xui/prose';
import type { DocSpan, GuideHeading } from '../core/model';

/** One piece of a rendered page: a run of markdown, a heading, or a fence. */
interface Block {
  kind: 'markdown' | 'heading' | 'code';
  text: string;
  level: number;
  /** The generator's anchor for a heading; empty when it has none, which is every level past three. */
  id: string;
  language: string;
  filename: string;
  tokens: XuiCodeLine[] | null;
}

/**
 * A guide page's body — docs/plan/25 § 4.
 *
 * The markdown arrives with its snippets resolved (§ P2) and its links already pointing at routes
 * the site serves (`PageLinks.WithSiteLinks`), and is rendered here; the *styling* is `@xui/prose`
 * (X2), which is why this emits bare `<p>` and `<ul>` with no classes on them. A markdown renderer
 * is not a UI component and xUI does not ship one — but typography that follows the theme's tokens
 * in both themes is, and that half is the package's.
 *
 * Fences go to `@xui/code-block` (X1) with whatever classification the generator produced for them,
 * so an example is coloured by the compiler that checked it rather than by a grammar in the browser.
 */
@Component({
  selector: 'docs-prose',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiCodeBlock, XuiProse, XuiProseAnchor],
  host: { class: 'block' },
  template: `
    <article xuiProse>
      @for (block of blocks(); track $index) {
        @switch (block.kind) {
          @case ('code') {
            <xui-code-block
              [class]="codeClass(block)"
              [code]="block.text"
              [tokens]="block.tokens"
              [language]="block.language"
              [filename]="block.filename"
            />
          }
          @case ('heading') {
            <!-- X4, and it has to be a real component: the id, the hover link and the scroll-margin
                 that clears the sticky header all come from it. A heading written into the markdown
                 string instead loses its id outright — Angular's sanitizer drops an id attribute
                 from anything bound through innerHTML, which left every anchor on the site, and
                 every row of every outline beside it, pointing at nothing. -->
            @if (block.level === 2 && block.id) {
              <h2 [xuiProseAnchor]="block.id" [basePath]="basePath()"><span [innerHTML]="block.text"></span></h2>
            } @else if (block.level === 3 && block.id) {
              <h3 [xuiProseAnchor]="block.id" [basePath]="basePath()"><span [innerHTML]="block.text"></span></h3>
            } @else if (block.level === 2) {
              <h2 [innerHTML]="block.text"></h2>
            } @else if (block.level === 3) {
              <h3 [innerHTML]="block.text"></h3>
            } @else {
              <h4 [innerHTML]="block.text"></h4>
            }
          }
          @default {
            <div [innerHTML]="block.text"></div>
          }
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
   * The page's outline, in the order its headings appear.
   *
   * ⚠ **Taken rather than derived.** The ids are what the table of contents links to and what the
   * generator's own link check validates, so a second derivation here is a second answer: this
   * matches by position, which the generator's parse and this one agree on for all 225 pages.
   */
  readonly headings = input<readonly GuideHeading[]>([]);

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
    const outline = this.headings();
    let buffer: string[] = [];
    let fence = 0;
    let heading = 0;

    const push = (block: Partial<Block> & Pick<Block, 'kind' | 'text'>) =>
      blocks.push({ level: 0, id: '', language: '', filename: '', tokens: null, ...block });

    const flush = () => {
      if (buffer.length > 0) {
        push({ kind: 'markdown', text: render(buffer.join('\n')) });
        buffer = [];
      }
    };

    for (let index = 0; index < lines.length; index++) {
      const line = lines[index];

      if (line.startsWith('```')) {
        flush();

        const info = line.slice(3).trim().split(' ');
        const code: string[] = [];

        index++;

        while (index < lines.length && !lines[index].startsWith('```')) {
          code.push(lines[index]);
          index++;
        }

        const runs = classified[String(fence)];

        push({
          kind: 'code',
          text: code.join('\n'),
          language: info[0] ?? '',
          filename: info.find(word => word.startsWith('title='))?.slice('title='.length) ?? '',
          tokens: runs ? runs.map(row => row.map(([text, kind]) => ({ text, kind: tokenKind(kind) }))) : null
        });

        fence++;

        continue;
      }

      const marked = /^(#{2,6}) +(.+?)\s*$/.exec(line);

      if (!marked) {
        buffer.push(line);

        continue;
      }

      flush();

      // The outline stops at three, so a deeper heading takes no id and consumes no entry — the same
      // rule the generator's parse applies, which is what keeps the two lists in step.
      const level = marked[1].length;

      push({
        kind: 'heading',
        text: renderInline(marked[2]),
        level,
        id: level <= 3 ? (outline[heading++]?.Id ?? '') : ''
      });
    }

    flush();

    return blocks;
  });

  /**
   * ⚠ `xuiProse` styles `<pre>`, and the code block styles its own.
   *
   * A descendant rule beats the element's own class, so prose's margin and radius won over the
   * component's inside a document — a 16 px gap between a block's header and its body, and rounded
   * top corners under a square header. These two put the component back in charge; nothing else in
   * the two sets disagrees.
   */
  protected codeClass(block: Block): string {
    return block.language.length > 0 || block.filename.length > 0
      ? 'my-6 [&_pre]:my-0! [&_pre]:rounded-t-none!'
      : 'my-6 [&_pre]:my-0!';
  }
}

/**
 * The subset of markdown a guide page uses, and nothing else.
 *
 * No classes anywhere: `xuiProse` styles the elements, which is X2's whole point — the same markup
 * reads as one system with the rest of the site, in both themes, without this knowing a token name.
 *
 * ⚠ **The subset is what the pages contain, and it was smaller than they are.** Paragraphs, bullets,
 * code and bold were implemented; tables were not, and 110 of the 225 pages have one — they shipped
 * as a paragraph of `| pipes |`. So did 1 199 runs of italic, 113 blockquote lines, and every bullet
 * whose text wrapped onto a second line, which became a second bullet.
 */
function render(markdown: string): string {
  return markdown
    .split(/\n{2,}/)
    .map(chunk => chunk.trim())
    .filter(chunk => chunk.length > 0)
    .map(chunk => {
      if (chunk.startsWith('|')) {
        return table(chunk);
      }

      if (chunk.startsWith('>')) {
        return `<blockquote><p>${renderInline(items(chunk, /^>\s?/).join(' '))}</p></blockquote>`;
      }

      if (/^- /.test(chunk)) {
        return `<ul>${items(chunk, /^-\s+/).map(item => `<li>${renderInline(item)}</li>`).join('')}</ul>`;
      }

      if (/^\d+\. /.test(chunk)) {
        return `<ol>${items(chunk, /^\d+\.\s+/).map(item => `<li>${renderInline(item)}</li>`).join('')}</ol>`;
      }

      return `<p>${renderInline(chunk)}</p>`;
    })
    .join('\n');
}

/**
 * The lines of a list or a quote, with a wrapped line folded into the item it belongs to.
 *
 * A guide page's bullets are prose and run to two and three lines; one `<li>` per source line turned
 * every one of those into an item of its own.
 */
function items(chunk: string, marker: RegExp): string[] {
  const rows: string[] = [];

  for (const line of chunk.split('\n')) {
    if (marker.test(line)) {
      rows.push(line.replace(marker, ''));
    } else if (rows.length > 0) {
      rows[rows.length - 1] += ` ${line.trim()}`;
    }
  }

  return rows;
}

/** A pipe table, with its head when it has the `|---|` row that declares one. */
function table(chunk: string): string {
  const rows = chunk
    .split('\n')
    .filter(line => line.trim().startsWith('|'))
    .map(cells);
  const divider = (row: string[]) => row.every(cell => /^:?-+:?$/.test(cell));
  const head = rows.length > 1 && divider(rows[1]) ? rows[0] : null;
  const row = (tag: string, values: string[]) =>
    `<tr>${values.map(value => `<${tag}>${renderInline(value)}</${tag}>`).join('')}</tr>`;
  const body = rows
    .filter((values, index) => !divider(values) && !(head !== null && index === 0))
    .map(values => row('td', values))
    .join('');

  return `<table>${head === null ? '' : `<thead>${row('th', head)}</thead>`}<tbody>${body}</tbody></table>`;
}

/** One row's cells, with an escaped pipe kept out of the split it would otherwise land in. */
function cells(line: string): string[] {
  return line
    .trim()
    .replaceAll('\\|', '\u0001')
    .replace(/^\|/, '')
    .replace(/\|$/, '')
    .split('|')
    .map(value => value.replaceAll('\u0001', '|').trim());
}

/** The inline half — what a heading gets, and what a cell, an item and a paragraph get. */
function renderInline(markdown: string): string {
  const code: string[] = [];

  return (
    markdown
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      // ⚠ Code spans come out first and go back last. What is inside one is not markdown, and the
      // asterisk in `overflow-x-*` otherwise pairs with the next cell's and italicises the tags
      // between them.
      .replace(/`([^`]+)`/g, (_, text: string) => `\u0000${code.push(text) - 1}\u0000`)
      // Lazy rather than "up to the next asterisk": a bold run holds an italic one often enough to
      // matter, and holds a wrapped line more often than that.
      .replace(/\*\*([\s\S]+?)\*\*/g, '<strong>$1</strong>')
      // After the bold, so the pair that is left is emphasis — and the content may not begin or end
      // with a space, which is what keeps `2 * 3 * 4` from reading as one.
      .replace(/(?<![\w*])\*([^\s*][^*\n]*[^\s*]|[^\s*])\*(?![\w*])/g, '<em>$1</em>')
      .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2">$1</a>')
      .replace(/\u0000(\d+)\u0000/g, (_, index: string) => `<code>${code[Number(index)]}</code>`)
  );
}
