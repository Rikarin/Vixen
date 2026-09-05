// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The remesher is compiled with the optimiser on in every configuration, and stays so.</summary>
/// <remarks>
///     ⚠ Measured back to back on one machine on 2026-09-05: this suite is 327 s built the way a
///     Debug build used to build it, 122 s with <c>Optimize</c> on in
///     <c>Vixen.Geometry.Remeshing.csproj</c>, and 85 s in Release — 340 tests passing in each. See
///     <c>Vixen.Geometry.Uv.Tests.OptimisedInDebugTests</c> for the full reasoning, including why
///     the second test here is not redundant.
/// </remarks>
public class OptimisedInDebugTests {
    /// <summary>⚠ Absent is optimised; the flag is what carries the meaning, not the attribute.</summary>
    static bool IsOptimised(Assembly assembly) =>
        assembly.GetCustomAttribute<DebuggableAttribute>() is not { IsJITOptimizerDisabled: true };

    [Fact]
    public void The_remesher_is_built_with_the_optimiser_on() {
        Assert.True(
            IsOptimised(typeof(Remesher).Assembly),
            "Vixen.Geometry.Remeshing is compiled with the optimiser off. Its csproj sets "
            + "<Optimize Condition=\"'$(Configuration)' == 'Debug'\">true</Optimize> and something has "
            + "removed it: this suite goes from 122 s back to 327 s without it. See that file's remarks."
        );
    }

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
