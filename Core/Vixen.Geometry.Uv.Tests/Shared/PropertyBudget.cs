// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Geometry.Testing;

/// <summary>How many cases a property gets, which is not the same number on a build and overnight.</summary>
/// <remarks>
///     <para>
///         <b>A property test's value is proportional to how much of its space it sees, and the space
///         these draw from is far larger than a build can afford to walk.</b> The per-commit gate runs
///         each property a hundred times or so, which is enough to be a regression test and is not a
///         search. The nightly leg runs the same properties with this multiplier raised, which is the
///         search.
///     </para>
///     <para>
///         ⚠ <b>CsCheck's own <c>CsCheck_Iter</c> cannot do this, and that is why the helper exists.</b>
///         It sets the <i>default</i> iteration count, and every property here passes <c>iter:</c>
///         explicitly — a number chosen so the build stays fast on that particular property rather
///         than on all of them equally. An explicit argument wins over the default, so the
///         environment variable is read and silently does nothing, which is worse than not offering
///         it. This multiplies what each property already asked for and keeps their relative weights.
///     </para>
///     <para>
///         ⚠ <b>The seed is deliberately not pinned, and that is a decision rather than an
///         oversight.</b> CsCheck draws a fresh seed per run unless told otherwise, so every build
///         already explores somewhere new — which is how a one-in-twenty flake in the least-squares
///         solver was found at all. A nightly that re-ran a fixed seed with more iterations would
///         walk further down one path; this walks further down a different path every night. The
///         cost is that a nightly failure is not reproducible from the workflow alone, and the fix
///         for that is the seed CsCheck prints on failure: set <c>CsCheck_Seed</c> to it and the case
///         comes back exactly.
///     </para>
/// </remarks>
public static class PropertyBudget {
    /// <summary>The environment variable the nightly leg sets.</summary>
    public const string Variable = "VIXEN_PROPERTY_ITER";

    /// <summary>How much more than a build's worth of cases to run. One on a build.</summary>
    /// <remarks>
    ///     ⚠ Read once into a static, because a property that read the environment per case would
    ///     make the count depend on when the run started.
    /// </remarks>
    public static int Multiplier { get; } = Read();

    /// <summary>Whether this is the long run rather than the gate.</summary>
    public static bool IsNightly => Multiplier > 1;

    /// <summary>What a property should actually ask CsCheck for.</summary>
    /// <param name="onABuild">What the property is worth on the per-commit gate.</param>
    /// <returns>The same on a build, and <see cref="Multiplier" /> times it overnight.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="onABuild" /> is not positive.</exception>
    public static int Iterations(int onABuild) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(onABuild);

        // ⚠ Saturating rather than wrapping. A multiplier somebody typed an extra zero into should
        // give a run that takes too long and gets killed by the job's timeout, which is legible —
        // not a negative iteration count, which CsCheck reads as "none" and which would report a
        // green nightly having tested nothing.
        var scaled = (long) onABuild * Multiplier;

        return scaled > int.MaxValue ? int.MaxValue : (int) scaled;
    }

    static int Read() {
        var raw = Environment.GetEnvironmentVariable(Variable);

        if (string.IsNullOrWhiteSpace(raw)) {
            return 1;
        }

        // A value that is not a positive number is the gate's, not a crash: a workflow that typed
        // the variable wrong should run the ordinary suite rather than fail in a way that looks like
        // a defect in the code under test.
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 1;
    }
}
