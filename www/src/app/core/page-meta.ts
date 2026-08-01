// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { DOCUMENT, Injectable, inject } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { Router } from '@angular/router';

/** The site's own origin, which is what a canonical URL and an `og:url` have to be absolute against. */
export const ORIGIN = 'https://vixenengine.org';

/**
 * The tags a search engine and a link preview read — docs/plan/25 § 8.5.
 *
 * ⚠ **A prerendered page with no description is a page a search engine summarises by guessing**, and
 * these pages exist to be found: the whole argument for prerendering 4 000 of them rather than
 * rendering them in a worker is that a crawler reads what a reader reads. A title alone does not
 * finish that argument.
 *
 * Set per route rather than once at the root, because every value here is page-specific — and the
 * canonical link is written into the DOM rather than declared in `index.html` for the same reason.
 */
@Injectable({ providedIn: 'root' })
export class PageMeta {
  private readonly meta = inject(Meta);
  private readonly heading = inject(Title);
  private readonly document = inject(DOCUMENT);
  private readonly router = inject(Router);

  /**
   * @param description One sentence. Trimmed to what a result snippet shows rather than to what
   *   fits, because a description cut mid-clause reads worse than a short one.
   */
  set(description: string, options: { title?: string; path?: string; noindex?: boolean } = {}): void {
    const path = options.path ?? this.router.url.split('#')[0].split('?')[0];
    const url = `${ORIGIN}${path}`;
    const summary = clamp(description);
    const title = options.title ?? this.document.title;

    // ⚠ The title too, and not only the social tags. The route's own title strategy can only see the
    // URL, so a symbol page was titled `world — Vixen` — the slug, lowercased — while the page it
    // titles says `World`. A title is the strongest on-page signal a search engine reads, and the
    // slug is the one string on the page that is not what anything is called.
    if (options.title) {
      this.heading.setTitle(options.title);
    }

    this.meta.updateTag({ name: 'description', content: summary });
    this.meta.updateTag({ property: 'og:title', content: title });
    this.meta.updateTag({ property: 'og:description', content: summary });
    this.meta.updateTag({ property: 'og:url', content: url });
    this.meta.updateTag({ property: 'og:type', content: 'website' });
    this.meta.updateTag({ property: 'og:site_name', content: 'Vixen' });
    this.meta.updateTag({ name: 'twitter:card', content: 'summary_large_image' });
    this.meta.updateTag({ name: 'twitter:title', content: title });
    this.meta.updateTag({ name: 'twitter:description', content: summary });

    // ⚠ No `og:image`, deliberately. A card image that 404s is worse than no card image — the
    // preview renders an empty frame rather than falling back to text — and there is no artwork in
    // the repository yet. `src/app/core/hero.ts` is where that lands; add the tag with it.

    if (options.noindex) {
      this.meta.updateTag({ name: 'robots', content: 'noindex, follow' });
    } else {
      this.meta.removeTag("name='robots'");
    }

    this.canonical(url);
  }

  /**
   * One canonical link, moved rather than added.
   *
   * A single-page application that appended one per navigation would end up telling a crawler that
   * a page is canonically several others.
   */
  private canonical(url: string): void {
    const head = this.document.head;
    const existing = head.querySelector<HTMLLinkElement>('link[rel="canonical"]');
    const link = existing ?? head.appendChild(this.document.createElement('link'));

    link.setAttribute('rel', 'canonical');
    link.setAttribute('href', url);
  }
}

/** ~155 characters is what a result snippet shows; the cut lands on a word. */
function clamp(text: string): string {
  const collapsed = text.replace(/\s+/g, ' ').trim();

  if (collapsed.length <= 155) {
    return collapsed;
  }

  const cut = collapsed.slice(0, 155);

  return `${cut.slice(0, cut.lastIndexOf(' '))}…`;
}
