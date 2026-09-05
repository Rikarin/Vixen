// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The unwrapper is compiled with the optimiser on in every configuration, and stays so.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An unoptimised unwrapper is not a slower version of the same thing; it is most of
///         this repository's local test wall.</b> Measured back to back on one machine on
///         2026-09-05: this suite is 362 s built the way a Debug build used to build it, 124 s with
///         <c>Optimize</c> on in <c>Vixen.Geometry.Uv.csproj</c>, and 98 s in Release — 464 tests
///         passing in each. Two assemblies like this one were 48% of the whole suite's test CPU.
///     </para>
///     <para>
///         <b>Asserted rather than left to the csproj</b>, for the reason
///         <c>Vixen.Editor.App.Tests</c>' own assembly-attribute guard gives: a build property with
///         a paragraph of prose beside it is exactly the kind of thing that is deleted by somebody
///         tidying, and nothing fails — the suite simply becomes three times slower again, months
///         later, attributed to whatever else changed that week.
///     </para>
///     <para>
///         ⚠ <b><see cref="Optimize" /> and <c>Configuration</c> are two decisions and only one of
///         them is dangerous to move.</b> This asserts the first and asserts that the second has
///         <em>not</em> moved with it: <c>DEBUG</c> is still what decides
///         <see cref="JobScheduler.SafetyChecksEnabled" />, which is what <c>CheckApi</c> records
///         for that <c>public const bool</c> and what several suites assert. A day where this file
///         is green because the whole project quietly moved to Release is a day the second assertion
///         goes red.
///     </para>
/// </remarks>
public class OptimisedInDebugTests {
    /// <summary>
    ///     ⚠ <b>Absent is optimised.</b> The compiler emits no <see cref="DebuggableAttribute" /> at
    ///     all for some configurations, and emits one carrying
    ///     <see cref="DebuggableAttribute.DebuggingModes.DisableOptimizations" /> when the optimiser
    ///     is off. So the assertion is on the flag rather than on the attribute's presence: reading
    ///     it the other way round would make a missing attribute a failure, which is the shape that
    ///     goes red on a correct build.
    /// </summary>
    static bool IsOptimised(Assembly assembly) =>
        assembly.GetCustomAttribute<DebuggableAttribute>() is not { IsJITOptimizerDisabled: true };

    [Fact]
    public void The_unwrapper_is_built_with_the_optimiser_on() {
        Assert.True(
            IsOptimised(typeof(UvUnwrap).Assembly),
            "Vixen.Geometry.Uv is compiled with the optimiser off. Its csproj sets "
            + "<Optimize Condition=\"'$(Configuration)' == 'Debug'\">true</Optimize> and something has "
            + "removed it: this suite goes from 124 s back to 362 s without it. See that file's remarks."
        );
    }

    /// <summary>
    ///     The other half, and the one that says the speed was not bought by moving the suite across
    ///     the configuration line.
    /// </summary>
    /// <remarks>
    ///     ⚠ Skipped rather than inverted in Release, because this project is built in Release by
    ///     CI and by <c>--configuration Release</c>, where <c>DEBUG</c> is correctly absent. What it
    ///     watches is a <em>local</em> run that has stopped being a Debug run without anyone saying
    ///     so.
    /// </remarks>
    [Fact]
    public void The_optimiser_did_not_come_with_a_configuration_change() {
#if DEBUG
        Assert.True(
            JobScheduler.SafetyChecksEnabled,
            "This is a Debug build with the scheduler's safety checks compiled out, which no "
            + "combination of this repository's own settings produces. Optimize was meant to be the "
            + "only thing that moved."
        );
#else
        Assert.Skip("A Release build; DEBUG is correctly absent and there is nothing to hold in place.");
#endif
    }
}
