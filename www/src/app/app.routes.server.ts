// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { RenderMode, type ServerRoute } from '@angular/ssr';
import { GRAPH, GUIDE } from '../generated/manifest';
import { NODES } from '../generated/nodes';
import { RELEASES } from '../generated/releases';

/**
 * Documentation reads the same for everyone, so every page is rendered at build time and served as a
 * static asset — no worker invocation, no cold start, and the HTML carries the signatures, the facets
 * and the prose for anything that does not run JavaScript. docs/plan/25 § 8.1.
 */
export const serverRoutes: ServerRoute[] = [
  { path: '', renderMode: RenderMode.Prerender },
  { path: 'docs', renderMode: RenderMode.Prerender },
  { path: 'docs/api', renderMode: RenderMode.Prerender },
  {
    path: 'docs/api/:namespace',
    renderMode: RenderMode.Prerender,
    // ⚠ The union, because the two disagree for anything that is not a C# type. A shader's namespace
    // is `Raven.Library.Pipeline` and its page is `/docs/api/shaders/…`, so the segment its own
    // breadcrumb links to is one `GRAPH.namespaces` has never heard of — and nginx serves an
    // unprerendered path as a hard 404 rather than falling back to the shell.
    getPrerenderParams: async () => [
      ...new Set([
        ...GRAPH.namespaces.map(entry => entry.slug),
        ...NODES.map(node => node.slug.slice(0, node.slug.lastIndexOf('/')))
      ])
    ].map(namespace => ({ namespace }))
  },
  {
    path: 'docs/api/:namespace/:type',
    renderMode: RenderMode.Prerender,
    getPrerenderParams: async () =>
      NODES.map(node => {
        const separator = node.slug.lastIndexOf('/');

        return { namespace: node.slug.slice(0, separator), type: node.slug.slice(separator + 1) };
      })
  },
  {
    path: 'docs/guide/:area/:page',
    renderMode: RenderMode.Prerender,
    getPrerenderParams: async () =>
      GUIDE.map(entry => {
        const separator = entry.slug.indexOf('/');

        return { area: entry.slug.slice(0, separator), page: entry.slug.slice(separator + 1) };
      })
  },
  { path: 'docs/releases', renderMode: RenderMode.Prerender },
  {
    path: 'docs/releases/:version',
    renderMode: RenderMode.Prerender,
    getPrerenderParams: async () => RELEASES.map(entry => ({ version: entry.Version }))
  },
  ...['components', 'systems', 'controls', 'shaders', 'nodes', 'importers', 'attributes', 'diagnostics', 'log-events'].map(
    slug => ({ path: `docs/${slug}`, renderMode: RenderMode.Prerender }) as ServerRoute
  ),
  { path: '404', renderMode: RenderMode.Prerender },
  { path: '**', renderMode: RenderMode.Client }
];
