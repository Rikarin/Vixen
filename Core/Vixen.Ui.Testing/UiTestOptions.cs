// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Testing;

/// <summary>What a test is allowed to do while it waits, and where its pictures live.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The retry budget is counted in frames, not in seconds</b>, and that is the single
///         most important decision in this assembly. Cypress waits on a wall clock because a browser
///         it does not own is doing the work; here the test owns the loop, so "wait for the toast to
///         appear" means "run frames until it appears" — which is deterministic, as fast as the
///         machine can go, identical on a loaded CI runner and a quiet laptop, and replayable.
///     </para>
///     <para>
///         A wall-clock budget in the same place would have bought nothing and cost the property the
///         repository's test conventions ask for by name: no <c>Thread.Sleep</c>, no ambient clock,
///         no test that fails only when the machine is busy.
///     </para>
/// </remarks>
public sealed class UiTestOptions {
    /// <summary>How many frames a command may run before it gives up.</summary>
    /// <remarks>
    ///     Sixty — a second of a sixty-hertz game, which is far longer than any interface state
    ///     change driven by the document's own passes, and short enough that a genuinely wrong
    ///     selector fails while somebody is still looking at the screen.
    /// </remarks>
    public int RetryFrames { get; set; } = 60;

    /// <summary>How much the clock advances per frame.</summary>
    /// <remarks>
    ///     What the gesture recogniser reads. A long press has a threshold in milliseconds, so a
    ///     test that presses and then runs frames has to be advancing something — and it must be
    ///     this rather than <see cref="DateTime" />, or the same test reports a different gesture
    ///     when a breakpoint holds the frame.
    /// </remarks>
    public TimeSpan FrameDelta { get; set; } = TimeSpan.FromSeconds(1.0 / 60.0);

    /// <summary>Where reference images are read from and written to.</summary>
    /// <remarks>
    ///     Defaults to <c>__screenshots__</c> beside the test binary. A test project copies the
    ///     directory to its output the way it copies any other test data, and
    ///     <see cref="SourceBaselineDirectory" /> is where <see cref="UpdateBaselines" /> writes so
    ///     that an accepted change is something a human can commit.
    /// </remarks>
    public string? BaselineDirectory { get; set; }

    /// <summary>Where an accepted baseline is written, when that is not where it is read from.</summary>
    /// <remarks>
    ///     ⚠ Rewriting the copy beside the binary makes the run pass and changes nothing anybody can
    ///     commit — the next clean build restores the old picture and the "fix" evaporates. Set this
    ///     to the directory in the source tree; it defaults to walking up from the binary until a
    ///     directory holding the test project is found.
    /// </remarks>
    public string? SourceBaselineDirectory { get; set; }

    /// <summary>Where a failure writes what it saw.</summary>
    /// <remarks>
    ///     Under <c>artifacts/</c> so a CI workflow can upload the directory without knowing what is
    ///     in it, which is what makes a visual failure diagnosable from a build page rather than only
    ///     on the machine that produced it.
    /// </remarks>
    public string? ArtifactDirectory { get; set; }

    /// <summary>Whether a screenshot rewrites its reference rather than checking it.</summary>
    /// <remarks>
    ///     ⚠ Read from <c>VIXEN_UPDATE_SCREENSHOTS</c> and deliberately not defaulted to <c>true</c>
    ///     for a missing reference: a suite that rewrites its own expectations when they fail is a
    ///     suite that always passes. The first run of a new screenshot writes the reference and
    ///     fails, saying so, so that the picture is looked at once before it becomes the standard
    ///     everything else is measured against.
    /// </remarks>
    public bool UpdateBaselines { get; set; } =
        Environment.GetEnvironmentVariable("VIXEN_UPDATE_SCREENSHOTS") is "1" or "true" or "TRUE";

    /// <summary>How far a screenshot may drift from its reference before it has changed.</summary>
    /// <remarks>
    ///     Exact by default, which a GPU suite cannot be. Nothing here is rendered by a driver — the
    ///     software rasteriser is the same arithmetic on every machine — so a tolerance would only
    ///     ever hide a real difference. See <see cref="Visual.ImageTolerance" />.
    /// </remarks>
    public Visual.ImageTolerance Tolerance { get; set; } = Visual.ImageTolerance.Exact;

    /// <summary>What the interface is drawn on, where nothing else has been drawn.</summary>
    /// <remarks>
    ///     Opaque, and not transparent black. A screenshot with an alpha channel nobody composited
    ///     looks identical to a broken one in every image viewer, so the reference would be a picture
    ///     of a bug nobody could see.
    /// </remarks>
    public Core.Mathematics.Color4 Background { get; set; } = new(0.09f, 0.10f, 0.12f, 1f);
}
