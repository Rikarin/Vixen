// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// Jolt's initialisation is process-global — one allocator, one factory, one registry of shape types
// (see JoltRuntime). Two test collections running at once therefore take that global up and down
// underneath one another, and what comes out is a native abort with no managed stack rather than a
// failed assertion. Vixen.Physics.Tests serialises for the same reason and says so there too.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
