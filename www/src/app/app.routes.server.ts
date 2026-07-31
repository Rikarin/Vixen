// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { RenderMode, type ServerRoute } from '@angular/ssr';
import { GRAPH, GUIDE } from '../generated/manifest';
import { NODES } from '../generated/nodes';

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
    getPrerenderParams: async () => GRAPH.namespaces.map(entry => ({ namespace: entry.slug }))
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
  ...['components', 'systems', 'controls', 'shaders', 'nodes', 'importers', 'attributes', 'diagnostics', 'log-events'].map(
    slug => ({ path: `docs/${slug}`, renderMode: RenderMode.Prerender }) as ServerRoute
  ),
  { path: '404', renderMode: RenderMode.Prerender },
  { path: '**', renderMode: RenderMode.Client }
];
