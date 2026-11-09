// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A clock that stands where a test puts it, so a stamp's age is the test's to choose.</summary>
/// <param name="at">The moment it stands at, in UTC.</param>
/// <remarks>
///     ⚠ <b>Stopped at the moment of a write, it makes "the rewrite landed in the same tick" a fact of
///     the fixture rather than of the machine.</b> <c>PaintCanvasStore</c> trusts a stamp alone once it
///     is older than the file system's resolution; with the system clock, a test that stalled for that
///     long between its write and its read would test the other branch and pass for the wrong reason.
/// </remarks>
sealed class StoppedClock(DateTime at) : TimeProvider {
    /// <summary>Where it stands; moving it is the only way it moves.</summary>
    public DateTime At { get; set; } = DateTime.SpecifyKind(at, DateTimeKind.Utc);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(At, DateTimeKind.Utc));
}
