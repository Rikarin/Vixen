// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GRAPH } from '../../generated/manifest';

@Component({
  selector: 'docs-home',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <main class="mx-auto max-w-3xl px-4 py-24">
      <h1 class="text-foreground text-4xl font-semibold tracking-tight">Vixen</h1>
      <p class="text-foreground-muted mt-4 text-lg leading-relaxed">
        A .NET 10 game engine <em>and</em> application framework: the same stack that ships a game
        ships Photoshop- or Blender-class desktop tooling. The editor is written in the engine, which
        is the proof.
      </p>

      <div class="mt-8 flex flex-wrap gap-3">
        <a routerLink="/docs" class="bg-primary text-on-primary rounded-lg px-4 py-2 text-sm font-medium">
          What it offers
        </a>
        <a routerLink="/docs/api" class="border-border rounded-lg border px-4 py-2 text-sm font-medium">
          API reference
        </a>
      </div>

      <p class="text-foreground-subtle mt-12 text-sm">
        {{ total }} types, classified as what they are — components, systems, controls, shaders — and
        read from the engine's own source at every build.
      </p>
    </main>
  `
})
export class Home {
  protected readonly total = GRAPH.total;
}
