// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit.Sdk;
using Xunit.v3;

// A scheduler owns real threads and there are only eight scheduler slots in the process, so tests
// that each stand one up cannot run at the same time as each other. Serialising them also stops the
// timing-sensitive assertions from measuring the other tests' load.
[assembly: Parallelization(Mode = ParallelMode.None)]
