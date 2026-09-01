// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// ⚠ `Strings` is process-wide, and every panel in this assembly is a live consumer of it. Its
// catalog is one static `Signal<StringCatalog>` — see its own remarks for why a language is a
// property of the person rather than of a window — and an `@expr` in a `.vxml` that shows a word is
// a region-scoped `Effect` reading it, so opening a panel adds an edge to that one node and closing
// it takes the edge out again. The signal graph is single-threaded by contract and its edge lists
// are plain arrays with nothing interlocked, so two test classes standing editors up at once are two
// threads doing `--liveConsumerCount` on the same producer. The count goes negative and the next
// detach indexes `liveConsumers[-1]`.
//
// That is issue #365, and the test it took down was `Every_registered_panel_survives_being_closed_
// and_reopened` — which is not a coincidence: it opens and closes *every* registered panel, so it
// does more of that edge churn on `Strings` than anything else here. It failed in a full run and
// passed on its own, which reads as a timing flake and is not one; it is a data race, and running
// alone is simply running with nobody to race.
//
// `Vixen.Editor.Ui.Tests` and `Vixen.Editor.Core.Tests` have carried this attribute for the same
// reason since they were written, and `Vixen.Ui.Controls.Tests` reached the same place by a shared
// collection after the same kind of failure. This assembly — the one that builds whole editors — is
// the one that never got it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
