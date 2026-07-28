// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// Jolt's initialisation is process-global — one allocator, one factory, one registry of shape types
// (see JoltRuntime). The same reasoning as Vixen.Physics.Tests, at more length there: two
// collections running at once take that global up and down underneath one another, and what comes
// out is a native abort with no managed stack rather than a failed assertion.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
