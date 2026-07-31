// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CodeBlock } from './code-block';

/** One piece of a rendered page: a run of markdown, or a fence. */
interface Block {
  kind: 'markdown' | 'code';
  text: string;
  language: string;
  filename: string;
}

/**
 * ⚠ Stand-in for `@xui/prose` — docs/plan/25 § Part 9, X2. See ./README.md.
 *
 * The guide's body arrives as markdown with its snippets already resolved (§ P2), and this renders
 * the little of it a documentation page needs: headings with anchors, paragraphs, lists, and fences
 * handed to the code block. A full renderer belongs behind X2 rather than here, where it would be
 * thrown away.
 */
@Component({
  selector: 'docs-prose',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CodeBlock],
  host: { class: 'block' },
  template: `
    @for (block of blocks(); track $index) {
      @if (block.kind === 'code') {
        <docs-code-block class="my-6" [code]="block.text" [language]="block.language" [filename]="block.filename" />
      } @else {
        <div class="space-y-4" [innerHTML]="block.text"></div>
      }
    }
  `
})
export class Prose {
  readonly markdown = input.required<string>();

  protected readonly blocks = computed<Block[]>(() => {
    const blocks: Block[] = [];
    const lines = this.markdown().replaceAll('\r\n', '\n').split('\n');
    let buffer: string[] = [];

    const flush = () => {
      if (buffer.length > 0) {
        blocks.push({ kind: 'markdown', text: render(buffer.join('\n')), language: '', filename: '' });
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

      blocks.push({ kind: 'code', text: code.join('\n'), language: info[0] ?? '', filename });
    }

    flush();

    return blocks;
  });
}

/** The subset of markdown a guide page uses, and nothing else. */
function render(markdown: string): string {
  const escaped = markdown
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');

  return escaped
    .replace(/^### (.+)$/gm, (_, text) => heading(3, text))
    .replace(/^## (.+)$/gm, (_, text) => heading(2, text))
    .replace(/`([^`]+)`/g, '<code class="bg-surface rounded px-1 py-0.5 font-mono text-[0.9em]">$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong class="font-semibold">$1</strong>')
    .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a class="text-primary hover:underline" href="$2">$1</a>')
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

        return `<ul class="list-disc space-y-1 ps-6">${items}</ul>`;
      }

      return `<p class="text-foreground-muted leading-relaxed">${trimmed}</p>`;
    })
    .join('\n');
}

function heading(level: number, text: string): string {
  const id = text
    .toLowerCase()
    .replace(/[^\w\- ]/g, '')
    .replaceAll(' ', '-');
  const size = level === 2 ? 'mt-10 text-xl' : 'mt-6 text-lg';

  return `<h${level} id="${id}" class="${size} text-foreground scroll-mt-24 font-semibold">${text}</h${level}>`;
}
