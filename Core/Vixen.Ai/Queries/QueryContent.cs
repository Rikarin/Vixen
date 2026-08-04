// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Curves;

namespace Vixen.Ai;

/// <summary>Which shape of candidates a generator makes, as a file names it.</summary>
/// <remarks>
///     ⚠ <b>Six kinds and a seventh that is a name.</b> The shipped generators are arithmetic and a
///     file can describe every one of them with four numbers; anything a project writes is
///     <see cref="Registered" />, resolved off the resolver by name. An enum with a member per
///     project-defined generator would be one this repository has to grow for other people's games.
/// </remarks>
public enum QueryGeneratorKind : byte {
    /// <summary>A square grid on the ground.</summary>
    Grid,

    /// <summary>A ring at a fixed radius.</summary>
    Circle,

    /// <summary>Rings between two radii.</summary>
    Donut,

    /// <summary>A fan in front of the agent, aimed at the context.</summary>
    Cone,

    /// <summary>The one point the agent is standing on.</summary>
    CurrentLocation,

    /// <summary>Something the game registered by name on the resolver.</summary>
    Registered
}

/// <summary>Which reading a test takes, as a file names it.</summary>
public enum QueryTestKind : byte {
    /// <summary>How far the point is from the agent or from the context, in metres.</summary>
    Distance,

    /// <summary>How far in front of the agent the point is, in <c>[-1,1]</c>.</summary>
    Dot,

    /// <summary>Something the game registered by name on the resolver.</summary>
    Registered
}

/// <summary>One generator, as a file holds it.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable</b>, for the reason <c>UtilityConsiderationContent</c>'s remarks
///     give: the YAML binder takes part only in members it can write on both sides, so a get-only
///     collection round-trips to nothing.
/// </remarks>
[DataContract("QueryGenerator")]
public sealed class QueryGeneratorContent {
    /// <summary>Which shape it makes.</summary>
    public QueryGeneratorKind Kind { get; set; }

    /// <summary>The registered generator's name, for <see cref="QueryGeneratorKind.Registered" />.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>How far it reaches: a grid's extent, a circle's radius, a donut's outer, a cone's.</summary>
    public float Extent { get; set; } = 10f;

    /// <summary>A grid's spacing, or a donut's inner radius.</summary>
    public float Inner { get; set; } = 1f;

    /// <summary>A donut's rings, or a cone's arcs.</summary>
    public int Rings { get; set; } = 3;

    /// <summary>A circle's or a donut's or a cone's points per ring.</summary>
    public int Points { get; set; } = 12;

    /// <summary>A cone's width, in degrees.</summary>
    public float Degrees { get; set; } = 90f;

    /// <summary>Whether it centres on the agent rather than on what the query is about.</summary>
    public bool AroundQuerier { get; set; } = true;
}

/// <summary>One test, as a file holds it: a reading, a purpose, bounds and a curve.</summary>
[DataContract("QueryTest")]
public sealed class QueryTestContent {
    /// <summary>Which reading it takes.</summary>
    public QueryTestKind Kind { get; set; }

    /// <summary>The registered test's name, for <see cref="QueryTestKind.Registered" />.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>What it is for.</summary>
    public QueryTestPurpose Purpose { get; set; } = QueryTestPurpose.Score;

    /// <summary>Whether a distance test measures from what the query is about rather than the agent.</summary>
    public bool FromContext { get; set; }

    /// <summary>The reading that normalises to zero.</summary>
    public float Minimum { get; set; }

    /// <summary>The reading that normalises to one.</summary>
    public float Maximum { get; set; } = 1f;

    /// <summary>A reading below this filters the point, for a test that filters.</summary>
    public float Floor { get; set; } = float.NegativeInfinity;

    /// <summary>A reading above this filters the point, for a test that filters.</summary>
    public float Ceiling { get; set; } = float.PositiveInfinity;

    /// <summary>How much this test counts for, against the others.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>Which shape the normalised reading goes through.</summary>
    public ResponseCurveKind Curve { get; set; }

    /// <summary>Slope, or the bell's height.</summary>
    public float Slope { get; set; } = 1f;

    /// <summary>Exponent, steepness or width — whichever the shape has.</summary>
    public float Exponent { get; set; } = 1f;

    /// <summary>Vertical shift.</summary>
    public float Shift { get; set; }

    /// <summary>Horizontal shift.</summary>
    public float Centre { get; set; }

    /// <summary>The keys, for <see cref="ResponseCurveKind.Sampled" />.</summary>
    public List<UtilityCurveKeyContent> Keys { get; set; } = [];

    /// <summary>The curve this describes.</summary>
    /// <returns>The curve.</returns>
    /// <remarks>
    ///     ⚠ <b>The same <see cref="ResponseCurve" /> a utility consideration builds, from the same
    ///     fields.</b> doc 37 § D14: a test's scoring equation is a consideration's curve, so the
    ///     editor's curve control draws both and an author who has tuned one has tuned the other.
    /// </remarks>
    public ResponseCurve BuildCurve() => new() {
        Kind = Curve,
        Slope = Slope,
        Exponent = Exponent,
        Shift = Shift,
        Centre = Centre,
        Keys = Curve == ResponseCurveKind.Sampled && Keys.Count > 0
            ? [.. Keys.OrderBy(key => key.Time).Select(key => key.ToSample())]
            : null
    };
}

/// <summary>An environment query as a file: generators, then tests in order.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A list and not a graph, and doc 37 § D14 is explicit about why.</b> Unreal draws EQS
///         on a graph canvas, and what is actually on that canvas is a root with a fixed list of
///         children and no wiring decisions anywhere. So this is what it is: two lists, in order.
///     </para>
///     <para>
///         ⚠ <b>The order of the tests is the file's, and the runtime does not reorder it.</b> A
///         filtering test rejects a point and everything below it is skipped, so putting a trace above
///         a distance check is a raycast per point that a subtraction would have thrown away — which
///         is a real decision an author makes and a real cost the editor can show them.
///     </para>
/// </remarks>
[DataContract("EnvironmentQuery")]
public sealed class QueryContent {
    /// <summary>What an environment query is called on disk.</summary>
    public const string Extension = ".vxquery";

    /// <summary>The version this build writes and reads.</summary>
    public const int Current = 1;

    /// <summary>Which version wrote this file.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the query is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What makes the candidates.</summary>
    public List<QueryGeneratorContent> Generators { get; set; } = [];

    /// <summary>What filters and scores them, in order.</summary>
    public List<QueryTestContent> Tests { get; set; } = [];
}
