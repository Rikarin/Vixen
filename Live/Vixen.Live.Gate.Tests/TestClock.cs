// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Gate.Tests;

/// <summary>A clock a test moves by hand.</summary>
/// <remarks>
///     Hand-written rather than <c>Microsoft.Extensions.TimeProvider.Testing</c>: the whole of what
///     is needed here is "what time is it" and "make it later", and a package for six lines is a
///     dependency the repository would carry for one test project.
/// </remarks>
/// <param name="start">What time it is to begin with.</param>
sealed class TestClock(DateTimeOffset start) : TimeProvider {
    DateTimeOffset now = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>Makes it later.</summary>
    /// <param name="span">By how much.</param>
    public void Advance(TimeSpan span) => now += span;
}
