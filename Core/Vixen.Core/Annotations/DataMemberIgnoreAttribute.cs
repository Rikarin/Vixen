// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Excludes an otherwise-serialisable member from its type's serialised form — caches, back
///     references, and anything reconstructable from the members that are written.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class DataMemberIgnoreAttribute : Attribute;
