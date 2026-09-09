// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>"Never serialise a raw <c>Entity</c>", with a compiler behind it.</summary>
/// <remarks>
///     The pair of attributes is the whole trigger, and both halves of that are asserted here: a
///     component that only lives in a running world may name entities freely — several of the
///     engine's do — and one that reaches a file may not.
/// </remarks>
public class SerializedHandleTests {
    static Task<ImmutableArray<Diagnostic>> RunAsync(string source) =>
        AnalyzerHarness.RunAsync(source, new SerializedHandleAnalyzer());

    [Fact]
    public async Task A_serialised_component_may_not_name_an_entity() {
        var reported = await RunAsync(
            """
            using Vixen.Core;

            [Component]
            [DataContract]
            public struct AiFocus {
                public Entity Target;
                public float Weight;
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(SerializedHandleAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Target", AnalyzerHarness.Underlined(diagnostic));
    }

    /// <summary>
    ///     The shape every entity-naming component in the engine already has — <c>CameraTargets</c>,
    ///     <c>Possessing</c>, <c>PossessedBy</c>, <c>ViewTarget</c>, <c>PredictionSmoothing</c> — each
    ///     of which says in its own remarks that it is not <c>[DataContract]</c> because it names an
    ///     entity. This rule is that convention, checked.
    /// </summary>
    [Fact]
    public async Task A_component_that_never_reaches_a_file_may_name_whatever_it_likes() {
        var reported = await RunAsync(
            """
            using Vixen.Core;

            [Component]
            public struct CameraTargets {
                public Entity Follow;
                public Entity LookAt;
            }
            """
        );

        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_serialised_component_with_no_handle_is_the_ordinary_case() {
        var reported = await RunAsync(
            """
            using Vixen.Core;

            [Component]
            [DataContract]
            public struct Health {
                public float Value;
                public float Maximum;
            }
            """
        );

        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_handle_inside_a_collection_is_still_a_handle() {
        var reported = await RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Core;

            [Component]
            [DataContract]
            public struct Squad {
                public List<Entity> Members;
            }
            """
        );

        Assert.Equal(SerializedHandleAnalyzer.DiagnosticId, Assert.Single(reported).Id);
    }

    /// <summary>
    ///     ⚠ <b>The hole this rule had, and the one that mattered most.</b> A handle wrapped in a
    ///     plain struct is <em>more</em> dangerous than a bare one, not less: the bytes are inside
    ///     the component either way, the generated serializer writes them either way, and the
    ///     wrapper is exactly what a codebase reaches for once it has more than one kind of link.
    ///     The walk followed arrays and generic arguments and stopped at a named type's own fields,
    ///     so <c>List&lt;Entity&gt;</c> was refused and <c>Link</c> was waved through.
    /// </summary>
    [Fact]
    public async Task A_handle_one_struct_deep_is_still_written_down() {
        var reported = await RunAsync(
            """
            using Vixen.Core;

            [DataContract]
            public struct Link {
                public Entity Target;
                public float Weight;
            }

            [Component]
            [DataContract]
            public struct Leash {
                public Link Anchor;
            }
            """
        );

        var diagnostic = Assert.Single(reported);

        Assert.Equal(SerializedHandleAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Equal("Anchor", AnalyzerHarness.Underlined(diagnostic));
    }

    /// <summary>
    ///     ⚠ <b>A reference is where the walk stops, and stating the bound is the point.</b> A class
    ///     field is a pointer rather than bytes inside the component, its graph can be arbitrarily
    ///     large and cyclic, and whether it reaches a file at all depends on a serializer this rule
    ///     cannot see. Walking it would make the predicate unpredictable; refusing to walk it makes
    ///     the rule say exactly what it checks.
    /// </summary>
    [Fact]
    public async Task A_handle_behind_a_reference_is_out_of_this_rules_reach() {
        var reported = await RunAsync(
            """
            using Vixen.Core;

            [DataContract]
            public sealed class Bookmark {
                public Entity Target;
            }

            [Component]
            [DataContract]
            public struct Journal {
                public Bookmark Latest;
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     ⚠ A struct may name itself through a reference, and a walk that recursed on fields without
    ///     a bound would hang the build rather than fail it. The depth bound is what makes this
    ///     terminate, and this is the fixture that says so.
    /// </summary>
    [Fact]
    public async Task A_cyclic_shape_terminates() {
        var reported = await RunAsync(
            """
            using System.Collections.Generic;
            using Vixen.Core;

            public struct Node {
                public List<Node> Children;
                public float Weight;
            }

            [Component]
            [DataContract]
            public struct Tree {
                public Node Root;
            }
            """
        );

        Assert.Empty(reported);
    }

    /// <summary>
    ///     A plain type carrying neither attribute is not a component at all, and the rule is about
    ///     what a component writes rather than about the word <c>Entity</c> appearing in a file.
    /// </summary>
    [Fact]
    public async Task A_type_that_is_not_a_component_is_not_this_rules_business() {
        var reported = await RunAsync(
            """
            using Vixen.Core;

            [DataContract]
            public sealed class SaveHeader {
                public Entity Player;
            }
            """
        );

        Assert.Empty(reported);
    }
}
