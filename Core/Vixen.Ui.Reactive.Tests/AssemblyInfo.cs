// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// The graph is single-threaded by design, and several of its knobs — the owning thread, the
// per-thread default scheduler, the write epoch — are process- or thread-wide by construction.
// Running two test classes at once on two threads would have them stepping on each other's global
// state and reporting failures that say nothing about the code. The suite is small and fast; the
// parallelism is not worth what it would cost in flakiness.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
