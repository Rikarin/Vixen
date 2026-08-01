// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

/**
 * The landing page's hero media — the editor, if there is a picture of it.
 *
 * ⚠ **There is no screenshot in the repository, so there is none on the page.** A landing page's
 * hero is the one place where a mock-up is indistinguishable from a lie: a rendering of an editor
 * nobody has run is a claim about a product rather than a picture of one. So this is a slot, not a
 * placeholder — until a real recording exists the hero is the code sample, which is an honest
 * picture of what using the engine looks like and never goes stale.
 *
 * **To fill it:** drop the file into `www/public/hero/` and point `HERO` at it.
 *
 * | File | Set |
 * |---|---|
 * | `editor.webm` (a loop of the editor, muted, ~10 s) | `{ kind: 'video', src: '/hero/editor.webm', type: 'video/webm', poster: '/hero/editor.png' }` |
 * | `editor.png` (a still, ≥ 2× the rendered size) | `{ kind: 'image', src: '/hero/editor.png' }` |
 *
 * **The other slot is the social card.** `page-meta.ts` emits no `og:image`, because a card image
 * that 404s renders an empty frame rather than falling back to text. When `public/social.png` exists
 * — 1200×630, the name and one line — add the tag beside the other `og:` ones.
 *
 * A `poster` matters more than it looks: without one the first frame of a `webm` is whatever the
 * encoder chose, and on a slow connection that is the hero for several seconds. And `alt` is what a
 * screen reader is told the engine looks like, so it should describe the scene rather than say
 * "screenshot".
 */
export type Hero =
  | { kind: 'none' }
  | { kind: 'image'; src: string; alt: string }
  | { kind: 'video'; src: string; type: string; poster?: string; alt: string };

export const HERO: Hero = { kind: 'none' };
