// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Samples.EcsStressTest;

/// <summary>An orbiting speed, so the movement system has something to integrate.</summary>
/// <remarks>
///     In its own <c>*Components.cs</c> file, which is what the CA1051 exemption in .editorconfig
///     keys on. A component is public mutable fields; a file that holds components says so in its
///     name, and everything else in the project still answers to the rule.
/// </remarks>
public struct Orbit {
    /// <summary>Radians per second.</summary>
    public float Speed;

    /// <summary>How far from the centre.</summary>
    public float Radius;

    /// <summary>Where it is now.</summary>
    public float Angle;
}
