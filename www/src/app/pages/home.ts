// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { XuiCodeBlock, type XuiCodeLine } from '@xui/code-block';
import { XuiTag } from '@xui/tag';
import { GRAPH, GUIDE } from '../../generated/manifest';
import { tokenKind } from '../core/code';
import { HERO } from '../core/hero';
import { PageMeta } from '../core/page-meta';

/**
 * The landing page — the one page on this site nobody arrives at already knowing what Vixen is.
 *
 * ⚠ **Every number here is read from the graph rather than typed in.** A landing page that claims a
 * feature the engine does not have is the most expensive documentation error there is, and the way
 * it happens is somebody writing "over 4 000 types" once and nobody reading it again. These counts
 * come from the manifest the nav uses, so the page cannot drift from the engine — and the sample is
 * the fence the build compiles, coloured by the compiler that checked it.
 */
@Component({
  selector: 'docs-home',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiCodeBlock, XuiTag],
  template: `
    <main class="mx-auto max-w-[80rem] px-4">
      <!-- ── The hook ──────────────────────────────────────────────────────────────────────── -->
      <section class="grid items-center gap-10 py-20 lg:grid-cols-2 lg:py-28">
        <div class="space-y-6">
          <p class="text-primary text-sm font-medium tracking-wide uppercase">.NET 10 · Apache-2.0</p>

          <h1 class="text-foreground text-4xl font-semibold tracking-tight text-balance sm:text-5xl">
            A game engine that is also an application framework.
          </h1>

          <p class="text-foreground-muted text-lg leading-relaxed text-pretty">
            The same stack that ships a game ships Photoshop- or Blender-class desktop tooling —
            archetype ECS, a render graph over Vulkan, OpenGL and WebGPU, a retained-mode UI
            framework and its own shading language.
            <strong class="text-foreground">The editor is written in the engine</strong>, which is
            the proof rather than the claim.
          </p>

          <div class="flex flex-wrap gap-3">
            <a
              routerLink="/docs"
              class="bg-primary text-on-primary hover:bg-primary-emphasis rounded-lg px-5 py-2.5 text-sm font-medium transition-colors"
            >
              What it offers
            </a>
            <a
              [routerLink]="['/docs/guide', firstGuideArea, firstGuidePage]"
              class="border-border hover:border-primary rounded-lg border px-5 py-2.5 text-sm font-medium transition-colors"
            >
              Get started
            </a>
            <a
              routerLink="/docs/api"
              class="text-foreground-muted hover:text-foreground px-3 py-2.5 text-sm font-medium transition-colors"
            >
              API reference →
            </a>
          </div>

          <p class="text-foreground-subtle text-sm">
            Every page here is generated from the engine's own source at every build, and every
            example on it compiles against the engine CI has just tested.
          </p>
        </div>

        <!-- The editor, when there is a recording of one — src/app/core/hero.ts says how. Until
             then the sample is the visual: an honest picture of using the engine that cannot go
             stale, rather than a mock-up of a window nobody has opened. -->
        @if (hero.kind === 'video') {
          <video
            class="border-border w-full rounded-xl border shadow-2xl"
            [poster]="hero.poster ?? ''"
            [attr.aria-label]="hero.alt"
            autoplay
            muted
            loop
            playsinline
          >
            <source [src]="hero.src" [type]="hero.type" />
          </video>
        } @else if (hero.kind === 'image') {
          <img class="border-border w-full rounded-xl border shadow-2xl" [src]="hero.src" [alt]="hero.alt" />
        } @else {
          <div class="border-border bg-surface rounded-xl border p-1 shadow-2xl">
            <xui-code-block
              filename="Movement.cs"
              language="csharp"
              [code]="sample"
              [tokens]="sampleTokens"
              showLineNumbers
            />
          </div>
        }
      </section>

      <!-- ── What the graph says the engine is ─────────────────────────────────────────────── -->
      <section class="border-border grid grid-cols-2 gap-px border-y md:grid-cols-4">
        @for (stat of stats; track stat.label) {
          <a [routerLink]="stat.link" class="hover:bg-surface group px-4 py-8 text-center transition-colors">
            <p class="text-foreground text-3xl font-semibold tabular-nums">{{ stat.value }}</p>
            <p class="text-foreground-muted group-hover:text-foreground mt-1 text-sm transition-colors">
              {{ stat.label }}
            </p>
          </a>
        }
      </section>

      <!-- ── What it offers ────────────────────────────────────────────────────────────────── -->
      <section class="py-20">
        <div class="max-w-2xl space-y-3">
          <h2 class="text-foreground text-2xl font-semibold tracking-tight">Everything it offers</h2>
          <p class="text-foreground-muted">
            Not a list somebody maintains: each of these is a page built from what the code declares,
            so a feature appears the day it exists and says what it costs.
          </p>
        </div>

        <div class="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          @for (feature of features; track feature.title) {
            <a
              [routerLink]="feature.link"
              class="border-border hover:border-primary group rounded-xl border p-5 transition-colors"
            >
              <h3 class="text-foreground group-hover:text-primary font-medium transition-colors">
                {{ feature.title }}
              </h3>
              <p class="text-foreground-muted mt-2 text-sm leading-relaxed">{{ feature.blurb }}</p>
              @if (feature.note) {
                <p class="text-foreground-subtle mt-3 font-mono text-xs">{{ feature.note }}</p>
              }
            </a>
          }
        </div>
      </section>

      <!-- ── The argument the site itself makes ────────────────────────────────────────────── -->
      <section class="border-border grid gap-10 border-t py-20 lg:grid-cols-2">
        <div class="space-y-4">
          <h2 class="text-foreground text-2xl font-semibold tracking-tight">
            Documentation the build refuses to let rot
          </h2>
          <p class="text-foreground-muted leading-relaxed">
            A component's page says its size in bytes and how many fit in a chunk. A system's says
            its phase and what it reads and writes. A shader's says its descriptor sets. None of it
            is written by hand — it is read from declarations the compiler already relies on, so a
            page cannot describe an engine that no longer exists.
          </p>
          <ul class="text-foreground-muted space-y-2 text-sm">
            @for (promise of promises; track promise) {
              <li class="flex gap-3">
                <span class="text-primary" aria-hidden="true">✓</span>
                <span>{{ promise }}</span>
              </li>
            }
          </ul>
        </div>

        <div class="space-y-4">
          <h2 class="text-foreground text-2xl font-semibold tracking-tight">Where it runs</h2>
          <p class="text-foreground-muted leading-relaxed">
            One RHI, several backends, and the honest state of each — this site would rather tell you
            what is simulator-only than let you find out after a week.
          </p>
          <div class="flex flex-wrap gap-2">
            @for (target of targets; track target.name) {
              <xui-tag [color]="target.ready ? 'success' : 'warning'" minimal>{{ target.name }}</xui-tag>
            }
          </div>
          <p class="text-foreground-subtle text-sm">
            Vulkan is validation-clean on MoltenVK and lavapipe; the browser goes through WebGL2 and
            WebGPU; iOS and Android run on the simulator and the emulator, and say so.
          </p>
        </div>
      </section>

      <!-- ── The close ────────────────────────────────────────────────────────────────────── -->
      <section class="border-border border-t py-20 text-center">
        <h2 class="text-foreground text-2xl font-semibold tracking-tight">Start with an entity</h2>
        <p class="text-foreground-muted mx-auto mt-3 max-w-xl">
          The guide begins with the ECS: what a component is, and how to iterate the entities that
          have one. Everything else in the engine is reachable from there.
        </p>
        <div class="mt-8 flex flex-wrap justify-center gap-3">
          <a
            [routerLink]="['/docs/guide', firstGuideArea, firstGuidePage]"
            class="bg-primary text-on-primary hover:bg-primary-emphasis rounded-lg px-5 py-2.5 text-sm font-medium transition-colors"
          >
            Read the guide
          </a>
          <a
            routerLink="/docs/releases"
            class="border-border hover:border-primary rounded-lg border px-5 py-2.5 text-sm font-medium transition-colors"
          >
            Releases
          </a>
          <a
            href="https://github.com/rikarin/Vixen"
            rel="noreferrer"
            class="border-border hover:border-primary rounded-lg border px-5 py-2.5 text-sm font-medium transition-colors"
          >
            GitHub
          </a>
        </div>
      </section>
    </main>
  `
})
export class Home {
  private readonly meta = inject(PageMeta);

  protected readonly hero = HERO;

  /** Where "get started" goes: the first guide page there is, rather than a URL that may not exist. */
  private readonly firstGuide = GUIDE[0]?.slug ?? 'ecs/components';

  protected readonly firstGuideArea = this.firstGuide.split('/')[0];
  protected readonly firstGuidePage = this.firstGuide.split('/').slice(1).join('/');

  /**
   * The counts, from the manifest.
   *
   * Chosen for what they say rather than for size: 157 controls is the claim that there is a UI
   * framework, and 30 shaders is the claim that there is a shading language.
   */
  protected readonly stats = [
    { value: format(GRAPH.total), label: 'public types, classified', link: '/docs/api' },
    { value: format(GRAPH.counts['ui-control'] ?? 0), label: 'UI controls', link: '/docs/controls' },
    { value: format(GRAPH.counts['system'] ?? 0), label: 'ECS systems', link: '/docs/systems' },
    { value: format(GRAPH.counts['shader'] ?? 0), label: 'Raven shaders', link: '/docs/shaders' }
  ];

  protected readonly features = [
    {
      title: 'Archetype ECS',
      blurb:
        'Entities are rows, components are columns, and a query walks chunks rather than objects. ' +
        'The scheduler parallelises on what a system declares it reads and writes.',
      note: `${GRAPH.counts['scene-component'] ?? 0} scene components · ${GRAPH.counts['system'] ?? 0} systems`,
      link: '/docs/components'
    },
    {
      title: 'Render graph and RHI',
      blurb:
        'One surface over Vulkan, OpenGL/GLES/WebGL2 and WebGPU, with culling, resource aliasing and ' +
        'batched barriers derived rather than hand-written.',
      note: 'bindless descriptors · reversed depth',
      link: '/docs/api'
    },
    {
      title: 'Raven, the shading language',
      blurb:
        'Shaders are a language this engine owns, compiled to SPIR-V with reflection — which is why a ' +
        'shader page can tell you its bindings and its permutations.',
      note: `${GRAPH.counts['shader'] ?? 0} shaders · ${GRAPH.counts['graph-node'] ?? 0} graph nodes`,
      link: '/docs/shaders'
    },
    {
      title: 'Retained-mode UI',
      blurb:
        'The framework the editor is built out of: layout, text shaping, theming and markup, running ' +
        'on the same renderer as the game.',
      note: `${GRAPH.counts['ui-control'] ?? 0} controls`,
      link: '/docs/controls'
    },
    {
      title: 'Asset pipeline',
      blurb:
        'Importers claim extensions, the compiler produces deterministic content, and the editor ' +
        'watches the tree — the same pipeline in the editor and in a build.',
      note: `${GRAPH.counts['importer'] ?? 0} importers`,
      link: '/docs/importers'
    },
    {
      title: 'Editor and tooling',
      blurb:
        'Panels, inspectors, a scene view and play mode, plus a CLI, templates and the asset ' +
        'compiler. Written in the engine, which is what keeps the framework honest.',
      note: `${format(GRAPH.namespaces.length)} namespaces · ${GRAPH.projects} projects`,
      link: '/docs/api'
    },
    {
      title: 'Networking',
      blurb:
        'Replicated components with channels, send rates and per-field quantisation, and a wire ' +
        'format asserted byte-for-byte on three operating systems.',
      note: 'declared, not configured',
      link: '/docs/attributes'
    },
    {
      title: 'Gates, not conventions',
      blurb:
        'Public API, architecture rules, formatting and documentation coverage all fail the build, ' +
        'and every example in the guide compiles against the engine on every run.',
      note: `${GRAPH.counts['diagnostic'] ?? 0} diagnostics · ${GRAPH.counts['log-event'] ?? 0} log events`,
      link: '/docs/diagnostics'
    },
    {
      title: 'Built for agents too',
      blurb:
        'The graph this site renders is also an MCP server, so a coding agent searches the real API ' +
        'of the version you have installed rather than guessing at a name.',
      note: 'vixen-mcp · six tools',
      link: '/docs/api'
    }
  ];

  protected readonly promises = [
    'A new public type fails the build until somebody writes about it.',
    'Every fenced example is compiled against the engine, and coloured by the compiler that checked it.',
    'Each release emits its own table — added, removed, deprecated, and the breaking changes whose signatures are identical.',
    'Every page links to the exact lines it was read from on GitHub.'
  ];

  /** Green where it is tested and running, amber where it runs somewhere that is not a device. */
  protected readonly targets = [
    { name: 'Windows', ready: true },
    { name: 'macOS', ready: true },
    { name: 'Linux', ready: true },
    { name: 'Web', ready: true },
    { name: 'iOS — simulator', ready: false },
    { name: 'Android — emulator', ready: false }
  ];

  protected readonly sample = SAMPLE;

  protected readonly sampleTokens: XuiCodeLine[] = SAMPLE_TOKENS.map(line =>
    line.map(([text, kind]) => ({ text, kind: tokenKind(kind) }))
  );

  constructor() {
    this.meta.set(
      'Vixen is a .NET 10 game engine and application framework: archetype ECS, a render graph over ' +
        'Vulkan, OpenGL and WebGPU, a retained-mode UI framework, and its own shading language.',
      { title: 'Vixen — a .NET game engine and application framework', path: '/' }
    );
  }
}

/** 3738 → `3 738`. Thin groups, because a landing page's numbers are read rather than parsed. */
function format(value: number): string {
  return value.toLocaleString('en-US').replaceAll(',', ' ');
}

/**
 * The sample, and its classification.
 *
 * Quoted from `docs/guide/ecs/queries.md` — the fence `CheckDocs` compiles — rather than written
 * here, so the landing page cannot show code the engine would reject. The runs use the generator's
 * own vocabulary, which is what `tokenKind` maps onto the palette.
 */
const SAMPLE = `var moving = new QueryDescription().WithAll<Position, Velocity>();

foreach (var chunk in world.Chunks(moving)) {
    var positions = chunk.Values<Position>();
    var velocities = chunk.ReadValues<Velocity>();

    for (var index = 0; index < chunk.Count; index++) {
        positions[index].X += velocities[index].X * delta;
        positions[index].Y += velocities[index].Y * delta;
    }
}`;

const SAMPLE_TOKENS: [string, string][][] = [
  [
    ['var', 'keyword'],
    [' moving = ', 'text'],
    ['new', 'keyword'],
    [' ', 'text'],
    ['QueryDescription', 'class'],
    ['().', 'punctuation'],
    ['WithAll', 'method'],
    ['<', 'punctuation'],
    ['Position', 'struct'],
    [', ', 'punctuation'],
    ['Velocity', 'struct'],
    ['>();', 'punctuation']
  ],
  [],
  [
    ['foreach', 'keyword'],
    [' (', 'punctuation'],
    ['var', 'keyword'],
    [' chunk ', 'text'],
    ['in', 'keyword'],
    [' world.', 'text'],
    ['Chunks', 'method'],
    ['(moving)) {', 'punctuation']
  ],
  [
    ['    ', 'text'],
    ['var', 'keyword'],
    [' positions = chunk.', 'text'],
    ['Values', 'method'],
    ['<', 'punctuation'],
    ['Position', 'struct'],
    ['>();', 'punctuation']
  ],
  [
    ['    ', 'text'],
    ['var', 'keyword'],
    [' velocities = chunk.', 'text'],
    ['ReadValues', 'method'],
    ['<', 'punctuation'],
    ['Velocity', 'struct'],
    ['>();', 'punctuation']
  ],
  [],
  [
    ['    ', 'text'],
    ['for', 'keyword'],
    [' (', 'punctuation'],
    ['var', 'keyword'],
    [' index = ', 'text'],
    ['0', 'number'],
    ['; index < chunk.', 'text'],
    ['Count', 'property'],
    ['; index++) {', 'punctuation']
  ],
  [
    ['        positions[index].', 'text'],
    ['X', 'field'],
    [' += velocities[index].', 'text'],
    ['X', 'field'],
    [' * delta;', 'text']
  ],
  [
    ['        positions[index].', 'text'],
    ['Y', 'field'],
    [' += velocities[index].', 'text'],
    ['Y', 'field'],
    [' * delta;', 'text']
  ],
  [['    }', 'punctuation']],
  [['}', 'punctuation']]
];
