// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Marks a type as an ECS component. The reflection generator assigns it a
///     <see cref="ComponentTypeId" /> and registers it into the per-assembly type registry at
///     module initialisation, so no type scanning happens at run time.
/// </summary>
/// <remarks>
///     ⚠ <b>With <see cref="DataContractAttribute" /> beside it, this is also what lets a scene place
///     one.</b> The engine's component generator declares every type carrying both to
///     <c>SceneComponentRegistry</c>, which is what puts it in the inspector, in the Add Component
///     menu, in a <c>.vxscene</c> and in a compiled one. The two attributes answer two questions —
///     "the ECS may attach it" and "it can be described and turned into bytes" — and a component that
///     answers only the first is a handle its own bridge writes, which is exactly the thing a scene
///     must not carry.
/// </remarks>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
public sealed class ComponentAttribute : Attribute;
