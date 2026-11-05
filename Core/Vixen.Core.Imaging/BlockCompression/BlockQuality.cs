// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>How hard the BC7 and BC6H encoders search.</summary>
/// <remarks>
///     <para>
///         Only those two have a search to spend time on. BC1, BC3, BC4 and BC5 have one line to fit
///         and no modes to choose between, so the setting changes nothing about them. Every level
///         writes valid blocks that any decoder reads; the difference is how many modes and
///         partitions are tried and how long the winner is refined.
///     </para>
///     <para>
///         <b>Measured, not promised.</b> The table of what each level costs and buys, per image and
///         on one named machine, is in the assembly's README, and
///         <c>Benchmarks/Vixen.Benchmarks.Imaging</c> reproduces it.
///     </para>
/// </remarks>
public enum BlockQuality {
    /// <summary>The single-subset modes, and one partition guess when those clearly fall short.</summary>
    Fast = 0,

    /// <summary>Every mode over the likeliest partitions. What a build should use.</summary>
    Balanced = 1,

    /// <summary>Every mode, far more partitions and an endpoint search on the winner.</summary>
    Best = 2
}
