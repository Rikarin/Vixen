// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// The document model is signal-backed, and the signal graph's write epoch is a process-wide counter
// incremented without a lock — it is single-threaded by design (see Vixen.Ui.Reactive.Tests, which
// disables parallelism for the same reason). Two test classes writing signals at once could drop an
// increment and leave a computed believing it is still clean, which would show up as a failure with
// nothing to do with the code. The suite runs in a couple of seconds either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
