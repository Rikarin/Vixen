// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Marks a type as an ECS component. The reflection generator assigns it a
///     <see cref="ComponentTypeId" /> and registers it into the per-assembly type registry at
///     module initialisation, so no type scanning happens at run time.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
public sealed class ComponentAttribute : Attribute;
