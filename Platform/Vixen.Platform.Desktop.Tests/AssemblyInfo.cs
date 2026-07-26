// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// SDL is a process-wide singleton with a reference-counted init, so two platforms starting and
// stopping it at once is not a race this code can win — the second one's SDL_Quit tears down the
// first one's video subsystem. Serialising the assembly is the honest fix; the tests take
// milliseconds and there is nothing to gain from overlapping them.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
