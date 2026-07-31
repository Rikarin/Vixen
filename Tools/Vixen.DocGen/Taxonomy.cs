// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;

namespace Vixen.DocGen;

/// <summary>Decides what kind of thing a type is — docs/plan/25 § 2.3.</summary>
/// <remarks>
///     <para>
///         Every rule reads something the engine already relies on at compile time: an attribute a
///         generator looks for, a base type, an interface. Nothing here is a list of type names that
///         somebody has to remember to extend, which is the only reason the classification can be
///         trusted a year from now.
///     </para>
///     <para>
///         ⚠ <b>Order matters and the order is most-specific-first.</b> A scene component is also a
///         component; a system is also a class; an importer is also a class. The first rule that
///         matches wins, so a new rule goes above the ones it refines rather than at the end.
///     </para>
/// </remarks>
static class Taxonomy {
    const string ComponentAttribute = "Vixen.Core.ComponentAttribute";
    const string DataContractAttribute = "Vixen.Core.DataContractAttribute";
    const string ReplicatedAttribute = "Vixen.Net.Replication.ReplicatedAttribute";
    const string NodeAttribute = "Vixen.Editor.NodeGraph.NodeAttribute";
    const string ImporterAttribute = "Vixen.Editor.Assets.ImporterAttribute";
    const string SystemInterface = "Vixen.Ecs.Systems.ISystem";
    const string BehaviorBase = "Vixen.Engine.Behaviors.Behavior";
    const string AttributeBase = "System.Attribute";
    const string IncrementalGenerator = "Microsoft.CodeAnalysis.IIncrementalGenerator";
    const string DiagnosticAnalyzer = "Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer";
    const string ControlsNamespace = "Vixen.Ui.Controls";

    /// <summary>Classifies one type.</summary>
    public static DocKind Of(INamedTypeSymbol type) {
        var attributes = type.GetAttributes()
            .Select(attribute => attribute.AttributeClass?.ToDisplayString())
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        // A component the ECS may attach, and — with [DataContract] beside it — one a scene can
        // place. The two attributes answer two different questions and the pair is what puts a type
        // in the Add Component menu, so the pair is its own kind.
        if (attributes.Contains(ComponentAttribute)) {
            return attributes.Contains(DataContractAttribute) ? DocKind.SceneComponent : DocKind.Component;
        }

        if (attributes.Contains(ReplicatedAttribute)) {
            return DocKind.ReplicatedComponent;
        }

        if (attributes.Contains(NodeAttribute)) {
            return DocKind.GraphNode;
        }

        if (attributes.Contains(ImporterAttribute)) {
            return DocKind.Importer;
        }

        if (Implements(type, SystemInterface)) {
            return DocKind.System;
        }

        if (Inherits(type, BehaviorBase)) {
            return DocKind.Behavior;
        }

        if (Inherits(type, AttributeBase)) {
            return DocKind.Annotation;
        }

        if (Implements(type, IncrementalGenerator) || Inherits(type, DiagnosticAnalyzer)) {
            return DocKind.Generator;
        }

        // Positional rather than structural, deliberately: the control base type is internal to the
        // UI framework, and a directive-shaped control that derives from nothing still belongs in
        // the controls catalogue. The namespace is the declaration a reader is looking at.
        if (type.ContainingNamespace.ToDisplayString()
            .StartsWith(ControlsNamespace, StringComparison.Ordinal)) {
            return DocKind.UiControl;
        }

        return type.TypeKind switch {
            TypeKind.Interface => DocKind.Interface,
            TypeKind.Enum => DocKind.Enum,
            TypeKind.Delegate => DocKind.Delegate,
            TypeKind.Struct => DocKind.Struct,
            _ => DocKind.Class
        };
    }

    /// <summary>The kebab-cased form the site filters on.</summary>
    public static string Slug(DocKind kind) => kind switch {
        DocKind.SceneComponent => "scene-component",
        DocKind.ReplicatedComponent => "replicated-component",
        DocKind.UiControl => "ui-control",
        DocKind.GraphNode => "graph-node",
        _ => kind.ToString().ToLowerInvariant()
    };

    static bool Implements(INamedTypeSymbol type, string interfaceName) =>
        type.AllInterfaces.Any(candidate =>
            string.Equals(candidate.ToDisplayString(), interfaceName, StringComparison.Ordinal));

    static bool Inherits(INamedTypeSymbol type, string baseName) {
        for (var current = type.BaseType; current is not null; current = current.BaseType) {
            if (string.Equals(current.ToDisplayString(), baseName, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}
