// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'docs-not-found',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="mx-auto max-w-2xl px-4 py-24 text-center">
      <p class="text-foreground-subtle font-mono text-sm">404</p>
      <h1 class="text-foreground mt-2 text-2xl font-semibold">There is nothing at this address.</h1>
      <p class="text-foreground-muted mt-4">
        The page may have been renamed by a release. Every version's pages stay reachable at their own
        prefix, and the current ones are under
        <a routerLink="/docs" class="text-primary hover:underline">/docs</a>.
      </p>
    </div>
  `
})
export class NotFound {}
