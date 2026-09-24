// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// ⚠ Added for issue #365, and the race it was added for can no longer happen here. Every panel in
// this assembly is a live consumer of `Strings`: an `@expr` in a `.vxml` that shows a word is a
// region-scoped `Effect` reading it. Until #1413 its catalog was one static `Signal<StringCatalog>`,
// so opening a panel added an edge to that one node and closing it took the edge out again, and two
// test classes standing editors up at once were two threads doing `--liveConsumerCount` on the same
// producer: the count went negative and the next detach indexed `liveConsumers[-1]`. The test it
// took down was `Every_registered_panel_survives_being_closed_and_reopened`, which opens and closes
// *every* registered panel. It failed in a full run and passed on its own — a data race, not a timing
// flake. #1413 made the node that announces a language change `[ThreadStatic]`, so each thread's
// panels attach to their own node and nothing is shared at the graph level any more.
//
// It stays, for two reasons that still hold. The *language* is still process-wide — see `Strings`'
// remarks for why it is a property of the person rather than of a window — and
// `StringCatalogChainTests` changes it, which a class building an editor next door would see; that is
// why `Vixen.Editor.Ui.Tests` carries the same attribute and `Vixen.Ui.Controls.Tests` has its
// `SharedCatalogue` collection. And this assembly has run serially since #365, so an order-dependence
// it grew since then would only show once it stopped — `IconContributionTests` records one that was
// found. Turning parallelism back on is a measured change of its own, not a side effect of #1413.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
