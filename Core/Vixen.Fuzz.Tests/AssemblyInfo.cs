// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit.Sdk;
using Xunit.v3;

// CaseGuardTests measures how far the whole process's heap moved while one case was running, because
// that is the only allocation figure a watchdog thread can read — so a test class allocating beside it
// is noise charged to whichever case happened to be in flight. The gate itself is one class and was
// therefore already serial; this costs it nothing and makes the guard's assertions mean what they say.
[assembly: Parallelization(Mode = ParallelMode.None)]
