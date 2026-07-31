// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

/**
 * The shape of what `Vixen.DocGen` emits — docs/plan/25 § 2.5.
 *
 * Declared here rather than generated, so the site's types are reviewed when the generator's output
 * changes rather than silently following it.
 */

/** A classified run of a signature. `["public", "keyword"]` on the wire — § 3.4. */
export type DocSpan = [text: string, kind: string];

export interface DocSource {
  Path: string;
  StartLine: number;
  EndLine: number;
  Url?: string;
}

export interface DocFacets {
  SizeBytes?: number;
  EntitiesPerChunk?: number;
  Phase?: string;
  Reads?: string[];
  Writes?: string[];
  RunsBefore?: string[];
  RunsAfter?: string[];
  Channel?: string;
  SendRate?: number;
  Priority?: number;
  Quantized?: { Field: string; Min: number; Max: number; Bits: number }[];
  Extensions?: string[];
  MenuPath?: string;
  MenuSummary?: string;
  Targets?: string[];
  AllowMultiple?: boolean;
  Stages?: string[];
  Permutations?: string[];
  DescriptorSets?: number;
  ShaderParameters?: number;
  VertexInputs?: string[];
  EmittedBy?: string[];
  Level?: string;
  Since?: string;
}

export interface DocMember {
  Id: string;
  Name: string;
  MemberKind: string;
  Signature: DocSpan[];
  Summary?: string;
  Returns?: string;
  IsStatic: boolean;
  Obsolete?: string;
  Source?: DocSource;
}

export interface DocReference {
  Id: string;
  Name: string;
  Area: string;
  Assembly: string;
}

export interface DocNode {
  Id: string;
  Kind: string;
  Name: string;
  QualifiedName: string;
  Namespace: string;
  Assembly: string;
  Area: string;
  Slug: string;
  Signature: DocSpan[];
  Summary?: string;
  Remarks?: string;
  BaseType?: string;
  Interfaces?: string[];
  Members?: DocMember[];
  SeeAlso?: string[];
  Obsolete?: string;
  Facets?: DocFacets;
  UsedBy?: DocReference[];
  UsedByCount?: number;
  Docs?: string;
  AlsoIn?: string[];
  IsGenerated?: boolean;
  IsPackable?: boolean;
  Source?: DocSource;
}

/** What the manifest carries for every node: enough for a list, a breadcrumb and a search result. */
/**
 * What the manifest carries for every node.
 *
 * ⚠ Deliberately thin: this ships in the initial bundle for all 3 679 nodes, so it holds what a
 * route, a nav entry and a filter need and nothing else. Summaries, members and sources live in the
 * namespace chunk the page loads anyway.
 */
export interface NodeSummary {
  id: string;
  kind: string;
  name: string;
  qualifiedName: string;
  namespace: string;
  area: string;
  slug: string;
  usedBy: number;
  docs: number;
}

export interface NamespaceSummary {
  name: string;
  slug: string;
  areas: string[];
  count: number;
}

export interface GraphIndex {
  solution: string;
  configuration: string;
  commit: string | null;
  projects: number;
  total: number;
  /** How many nodes of each kind, so the nav can show a count without the node list. */
  counts: Record<string, number>;
  namespaces: NamespaceSummary[];
}

export interface GuideHeading {
  Id: string;
  Text: string;
  Level: number;
}

export interface GuidePage {
  Title: string;
  Slug: string;
  Kind: string;
  Area: string;
  Summary: string;
  Api: string[];
  Tags: string[];
  Since?: string;
  Status: string;
  Related: string[];
  Body: string;
  Headings: GuideHeading[];
  Edit?: string;

  /**
   * Classified fences, keyed by the fence's position in the body — § 3.4.
   *
   * Absent for a language the build has no lexer for, and absent entirely for a page whose fences
   * are all in one. The renderer falls back to plain text rather than guessing, which is the honest
   * state for a language nothing has read.
   */
  Tokens?: Record<string, DocSpan[][]>;
}

export interface GuideSummary {
  title: string;
  slug: string;
  kind: string;
  area: string;
  summary: string;
  tags: string[];
  status: string;
  symbols: number;
}

/**
 * `T:Vixen.Ecs.World` → `vixen.ecs/world`.
 *
 * The same derivation the generator does, for the same reason it does it there: a URL kept beside an
 * id is a second thing that can disagree with the first. Having it here as well means a page can
 * link to a symbol it has an id for without loading the node list to look one up.
 */
export function slugOf(documentationId: string): string {
  const name = documentationId.length > 2 && documentationId[1] === ':' ? documentationId.slice(2) : documentationId;
  const separator = name.lastIndexOf('.');
  const namespace = separator < 0 ? '' : name.slice(0, separator);
  const type = separator < 0 ? name : name.slice(separator + 1);
  const sanitise = (value: string) =>
    value
      .replaceAll('`', '-')
      .replaceAll('+', '.')
      .replace(/[{}@, ]/g, '-')
      .toLowerCase();

  return namespace.length === 0 ? sanitise(type) : `${sanitise(namespace)}/${sanitise(type)}`;
}

/**
 * One row of the eager search tier — `[name, qualifiedName, kind, slug, usedBy]`, § Part 7.
 *
 * A tuple rather than an object, and the reason is 3 681 of them: property names repeated that many
 * times are the difference between a tier that loads on a keystroke and one that does not.
 */
export type SearchName = [name: string, qualifiedName: string, kind: string, slug: string, usedBy: number];

// ── Releases — § 6 ────────────────────────────────────────────────────────────────────────────

/** One row of a release's table. `Kind` is kebab-cased on the wire, as the store writes it. */
export interface Change {
  Kind:
    | 'added'
    | 'removed'
    | 'deprecated'
    | 'signature-break'
    | 'shape-break'
    | 'semantic-break'
    | 'engine-break';
  Id: string;
  Display: string;
  Taxonomy: string;
  Before?: string;
  After?: string;
  Note?: string;
}

export interface ReleaseRecord {
  Version: string;
  Date: string;
  Commit?: string;
  Types: number;
  Members: number;
  Bytes: number;
}

export interface ReleaseDetail {
  Release: ReleaseRecord;
  Previous?: string;
  Changes: Change[];
  Counts: Record<string, number>;
}

/** What the switcher and the release index need without loading a table. */
export interface ReleaseSummary {
  Version: string;
  Date: string;
  Types: number;
  Members: number;
  Breaking: number;
  HasTable: boolean;
}

/**
 * The sections of a release page, in the order a reader needs them: what will break first, what is
 * merely deprecated after, and what is new last.
 */
export const CHANGE_SECTIONS: { kind: Change['Kind']; title: string; blurb: string }[] = [
  { kind: 'removed', title: 'Removed', blurb: 'Gone. Anything calling it stops compiling.' },
  { kind: 'shape-break', title: 'Breaking — shape', blurb: 'Still there, and no longer usable the same way.' },
  { kind: 'signature-break', title: 'Breaking — signature', blurb: 'The declaration changed.' },
  {
    kind: 'engine-break',
    title: 'Breaking — engine',
    blurb: 'The signature is identical and the behaviour is not: layout, phase, ordering, bindings.'
  },
  { kind: 'semantic-break', title: 'Breaking — behaviour', blurb: 'Written by hand, because no declaration says it.' },
  { kind: 'deprecated', title: 'Deprecated', blurb: 'Still works, and will not always.' },
  { kind: 'added', title: 'Added', blurb: 'New surface.' }
];

/** The kinds the taxonomy indexes are built from — § 8.2. */
export const TAXONOMY: { slug: string; kind: string; title: string; blurb: string }[] = [
  { slug: 'components', kind: 'scene-component', title: 'Components', blurb: 'Data a scene can place on an entity.' },
  { slug: 'systems', kind: 'system', title: 'Systems', blurb: 'What runs over the world, and in which phase.' },
  { slug: 'controls', kind: 'ui-control', title: 'Controls', blurb: 'The UI framework’s widgets.' },
  { slug: 'shaders', kind: 'shader', title: 'Shaders', blurb: 'Raven shaders, with their bindings and permutations.' },
  { slug: 'nodes', kind: 'graph-node', title: 'Graph nodes', blurb: 'Nodes for the shader and VFX graphs.' },
  { slug: 'importers', kind: 'importer', title: 'Importers', blurb: 'What turns a file into an asset.' },
  { slug: 'attributes', kind: 'annotation', title: 'Annotations', blurb: 'The attributes the engine reads at compile time.' },
  { slug: 'diagnostics', kind: 'diagnostic', title: 'Diagnostics', blurb: 'Every VX code the tools emit.' },
  { slug: 'log-events', kind: 'log-event', title: 'Log events', blurb: 'Every stable log id.' }
];
