// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Declares that a member runs inside the frame loop and must not allocate. It is a contract
///     for the allocation analyzer and for whoever edits the method next, not a hint to the JIT.
/// </summary>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor | AttributeTargets.Class |
    AttributeTargets.Struct)]
public sealed class HotPathAttribute : Attribute;
