// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

/**
 * One import for the two things every page wants and neither should reach for separately: the
 * manifest, and the taxonomy the site is organised by.
 */
export { GRAPH, GUIDE } from '../../generated/manifest';
export { TAXONOMY } from './model';
