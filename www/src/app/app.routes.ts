// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import type { ResolveFn, Routes } from '@angular/router';
import { GUIDE_LOADERS, PAGE_LOADERS } from '../generated/loaders';
import { RELEASE_LOADERS } from '../generated/releases';
import type { DocNode, GuidePage, ReleaseDetail } from './core/model';

/**
 * Loads one type's detail.
 *
 * Resolved rather than fetched inside the page, so the prerendered HTML already carries the members,
 * the facets and the signature instead of a shell that fills in once the browser has hydrated.
 * The chunk is the whole namespace — § 8.1 — so the next type a reader opens is already loaded.
 */
export const symbol: ResolveFn<DocNode | undefined> = async route => {
  const namespace = route.paramMap.get('namespace') ?? '';
  const slug = `${namespace}/${route.paramMap.get('type') ?? ''}`;
  const chunks = Object.keys(PAGE_LOADERS).filter(key => key === namespace || key.startsWith(`${namespace}.`));

  for (const chunk of chunks) {
    const nodes = await PAGE_LOADERS[chunk]();
    const found = nodes.find(node => node.Slug === slug);

    if (found) {
      return found;
    }
  }

  return undefined;
};

export const guide: ResolveFn<GuidePage | undefined> = async route => {
  // From the parameters rather than from `route.url`: the snapshot's segments include the `guide`
  // the path matched on, and `guide.ecs.queries` is not a key any loader has.
  const slug = `${route.paramMap.get('area')}.${route.paramMap.get('page')}`;

  return GUIDE_LOADERS[slug] ? GUIDE_LOADERS[slug]() : undefined;
};

/** One release's committed table — § 6.2. */
export const release: ResolveFn<ReleaseDetail | undefined> = async route => {
  const version = route.paramMap.get('version') ?? '';

  return RELEASE_LOADERS[version] ? RELEASE_LOADERS[version]() : undefined;
};

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./pages/home').then(m => m.Home),
    title: 'Vixen — a .NET game engine and application framework'
  },
  {
    path: 'docs',
    loadComponent: () => import('./layout/docs-layout').then(m => m.DocsLayout),
    children: [
      {
        path: '',
        pathMatch: 'full',
        loadComponent: () => import('./pages/overview').then(m => m.Overview),
        title: 'What Vixen offers — Vixen'
      },
      {
        path: 'api',
        pathMatch: 'full',
        loadComponent: () => import('./pages/api-index').then(m => m.ApiIndex),
        title: 'API — Vixen'
      },
      {
        path: 'api/:namespace',
        pathMatch: 'full',
        loadComponent: () => import('./pages/namespace').then(m => m.NamespacePage),
        title: route => `${route.paramMap.get('namespace') ?? 'Namespace'} — Vixen`
      },
      {
        path: 'api/:namespace/:type',
        loadComponent: () => import('./pages/symbol').then(m => m.SymbolPage),
        resolve: { node: symbol },
        // From the manifest rather than the resolved data: the title strategy runs against a snapshot
        // whose `data` does not yet carry the resolver's result.
        // From the URL rather than from the node list: a title is not worth 924 kB of manifest, and
        // the last segment of the slug is the type's name with its case folded.
        title: route => `${route.paramMap.get('type') ?? 'Not found'} — Vixen`
      },
      {
        path: 'guide/:area/:page',
        loadComponent: () => import('./pages/guide').then(m => m.GuidePageComponent),
        resolve: { page: guide }
      },
      {
        path: 'releases',
        pathMatch: 'full',
        loadComponent: () => import('./pages/releases').then(m => m.ReleasesPage),
        title: 'Releases — Vixen'
      },
      {
        path: 'releases/:version',
        loadComponent: () => import('./pages/release').then(m => m.ReleasePage),
        resolve: { release },
        title: route => `${route.paramMap.get('version') ?? 'Release'} — Vixen`
      },
      ...['components', 'systems', 'controls', 'shaders', 'nodes', 'importers', 'attributes', 'diagnostics', 'log-events'].map(
        slug => ({
          path: slug,
          loadComponent: () => import('./pages/taxonomy').then(m => m.Taxonomy),
          data: { taxonomy: slug }
        })
      )
    ]
  },
  { path: '404', loadComponent: () => import('./pages/not-found').then(m => m.NotFound), title: 'Not found — Vixen' },
  { path: '**', loadComponent: () => import('./pages/not-found').then(m => m.NotFound), title: 'Not found — Vixen' }
];
