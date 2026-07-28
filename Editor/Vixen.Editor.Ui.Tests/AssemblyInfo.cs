// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

// `Strings` is process-wide, because a language is a property of the person using the editor rather
// than of a window — see its own remarks. Two test classes switching catalogs at once would see each
// other's language, which would show up as a failure with nothing to do with the code under test.
// The suite runs in a couple of seconds either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
