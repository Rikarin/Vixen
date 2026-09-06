// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Reflection.Tests;

/// <summary>The suites that write to the process-wide registry, held to one at a time.</summary>
/// <remarks>
///     ⚠ <b><see cref="TypeRegistry" /> is static and xunit runs test classes in parallel.</b> Two
///     suites that only <em>read</em> it can share it happily; two that register descriptors cannot,
///     and the failure is not a hang or a corruption — it is
///     <c>RegisteringATypeTwiceReplacesRatherThanDuplicates</c>, which reads <c>Count</c> either side
///     of one registration, failing for something another class did in between. That reads as a
///     defect in the registry rather than as a race in the harness, which is the expensive kind of
///     flake.
/// </remarks>
[CollectionDefinition(Name)]
public class TypeRegistryTestGroup {
    /// <summary>What the suites name.</summary>
    public const string Name = "TypeRegistry";
}
