// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>The tests that touch <see cref="UiDevelopment" />, which is process-wide.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A hook that turns hot reload on for every application in the process turns it on for
///         every test in the process too, and xunit runs test classes in parallel.</b> Without this
///         a test that set <c>UiDevelopment.Mount</c> mounted some *other* test's application —
///         which is exactly what happened: an assertion in <c>UiApplicationTests</c> came back with
///         a component from <c>HotReloadSeamTests</c>, in a window of the wrong size.
///     </para>
///     <para>
///         The fix is a shared collection rather than a lock, because the hazard is not a data race:
///         it is that two applications must not be constructed while one of them has taken over
///         mounting. Serialising the classes is the only thing that makes the static safe, and it is
///         the honest cost of the design — a process-wide hook is worth it for an application and
///         has to be paid for here.
///     </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialUiDevelopment {
    /// <summary>What the two classes name.</summary>
    public const string Name = "UiDevelopment";
}
