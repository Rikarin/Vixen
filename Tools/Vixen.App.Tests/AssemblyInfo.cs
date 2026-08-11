// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// Eight classes here build a whole VixenApplication, and each build creates a JobScheduler. A handle
// names its scheduler by index, so `JobScheduler`'s table is a fixed eight for the process — a
// deliberate constant, and one a game never approaches because it builds one application. Test
// classes run in parallel by default, so eight app-building classes overlapping is eight-plus live
// schedulers and `Register` throws.
//
// ⚠ It fails in whichever class happened to build ninth, not in the one that added the pressure —
// so the symptom is a stranger's test going red, with a count that changes between runs. Adding a
// seventh app-building class was enough to start it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
