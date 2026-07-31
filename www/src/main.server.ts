// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

import { bootstrapApplication, type BootstrapContext } from '@angular/platform-browser';
import { App } from './app/app';
import { serverConfig } from './app/app.config.server';

// ⚠ The context is not optional in Angular 22. Without it every render — including the route
// extraction that decides what to prerender — fails with NG0401 "Missing Platform", which reads like
// a configuration problem and is a missing argument.
export default (context: BootstrapContext) => bootstrapApplication(App, serverConfig, context);
