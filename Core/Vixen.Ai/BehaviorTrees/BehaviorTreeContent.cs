// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>One blackboard key, as a file holds it.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable, and that is not laziness.</b> The YAML binder takes part only in
///     members it can write on both sides, so a get-only collection is written out and then silently
///     skipped on load — a file that loses its contents by round-tripping. `AnimationGraphAsset`
///     records the same thing at more length.
/// </remarks>
[DataContract("BehaviorKey")]
public sealed class BehaviorKeyContent {
    /// <summary>What the key is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What it holds. One of the six.</summary>
    public BlackboardValueType Type { get; set; }
}

/// <summary>One decorator or service, as a file holds it: a type name and its fields.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A type <i>name</i> and a bag of named values, not a polymorphic tree.</b> A
///         discriminated hierarchy in a text asset is a tag people have to get right by hand, and
///         binding one needs reflection at load — which ADR-002 rules out. What resolves the name to
///         an object is <see cref="BehaviorNodeSchema" />, which is a table this assembly declares.
///     </para>
///     <para>
///         The values are strings because a field's type is the schema's business rather than the
///         file's: an <c>Aborts</c> is an enum, a <c>Seconds</c> is a float and a <c>Key</c> is a
///         blackboard key's name, and one string per field means the reader does not need to know
///         which is which before it has looked the type up.
///     </para>
/// </remarks>
[DataContract("BehaviorAttachment")]
public sealed class BehaviorAttachmentContent {
    /// <summary>Which node type this is, as <see cref="BehaviorNodeSchema" /> names it.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Its fields, by name.</summary>
    public Dictionary<string, string> Fields { get; set; } = [];

    /// <summary>How often a service runs, in seconds. Ignored for a decorator.</summary>
    public float Interval { get; set; } = 0.5f;

    /// <summary>How much to jitter that. Ignored for a decorator.</summary>
    public float RandomDeviation { get; set; }

    /// <summary>The decorators this one is built out of, for the two that take others.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two decorators in doc 37 § Part 3 take other decorators</b> — <c>Composite</c>
    ///         joins them with AND / OR / NOT, and <c>ConditionalLoop</c> repeats a node while one
    ///         holds — and without this list neither could be written in a file at all. They were
    ///         built in P1 and stayed unauthorable until somebody read the table against the schema.
    ///     </para>
    ///     <para>
    ///         Empty for every other type, and a nested row's own <c>Children</c> are ignored: an
    ///         expression tree of arbitrary depth is a thing an inspector cannot draw and a thing
    ///         nobody has asked for. One level is <i>"visible AND has ammo AND not fleeing"</i>,
    ///         which is what the seam exists for.
    ///     </para>
    /// </remarks>
    public List<BehaviorAttachmentContent> Children { get; set; } = [];
}

/// <summary>One node, as a file holds it.</summary>
[DataContract("BehaviorNodeContent")]
public sealed class BehaviorNodeContent {
    /// <summary>What it is called, on the canvas and in a diagnostic.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which node type it is, as <see cref="BehaviorNodeSchema" /> names it.</summary>
    /// <remarks>
    ///     A composite's type is <c>Selector</c>, <c>Sequence</c> and so on; a task's is the action
    ///     it runs. Both are looked up in the same table, which is what lets the search popup offer
    ///     them together and filter by what may go where.
    /// </remarks>
    public string Type { get; set; } = string.Empty;

    /// <summary>Its fields, by name.</summary>
    public Dictionary<string, string> Fields { get; set; } = [];

    /// <summary>Its children, in priority order.</summary>
    /// <remarks>
    ///     ⚠ <b>Order is stored, not derived from <see cref="X" />.</b> Unreal derives a composite's
    ///     child order from horizontal position on the canvas, which makes three ordinary gestures
    ///     dangerous — auto-layout silently reorders the tree, dragging a node six pixels changes
    ///     which branch wins, and a merge that resolves two positions produces a tree neither author
    ///     wrote with a diff showing only coordinates. doc 37 § D5.
    /// </remarks>
    public List<BehaviorNodeContent> Children { get; set; } = [];

    /// <summary>Its decorators, in evaluation order.</summary>
    public List<BehaviorAttachmentContent> Decorators { get; set; } = [];

    /// <summary>Its services.</summary>
    public List<BehaviorAttachmentContent> Services { get; set; } = [];

    /// <summary>Where the box sits on the canvas.</summary>
    /// <remarks>
    ///     In the file, for doc 11's reason: a layout somebody spent an afternoon on is authored data,
    ///     and re-laying it out on every open throws it away every time. It is <i>only</i> a position —
    ///     nothing about the tree's behaviour is read from it.
    /// </remarks>
    public float X { get; set; }

    /// <summary>And vertically.</summary>
    public float Y { get; set; }
}

/// <summary>A behaviour tree as a file: a blackboard, a root, and where the boxes are.</summary>
/// <remarks>
///     <para>
///         What a <c>.vxbt</c> holds, what the importer compiles and what a game loads. It is a
///         separate shape from <see cref="BehaviorTreeAsset" /> on purpose: the asset holds live
///         <see cref="BehaviorDecorator" /> objects and is what the compiler consumes, and this holds
///         the <i>data</i> those are built from. <see cref="BehaviorTreeContentCompiler" /> is the
///         one direction between them.
///     </para>
///     <para>
///         The blackboard travels with the tree rather than being a second asset. Unreal makes a
///         blackboard its own file and shares it between trees, which is genuinely useful and is a
///         thing this can grow — but a tree whose keys live somewhere else cannot be opened, read or
///         compiled on its own, and every diagnostic about a key becomes a diagnostic about a file
///         the author is not looking at.
///     </para>
/// </remarks>
[DataContract("BehaviorTreeContent")]
public sealed class BehaviorTreeContent {
    /// <summary>What a behaviour tree is written as.</summary>
    public const string Extension = ".vxbt";

    /// <summary>The version this build writes and reads.</summary>
    public const int Current = 1;

    /// <summary>Which version wrote this file.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the tree is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its blackboard's keys, in the order they were declared.</summary>
    /// <remarks>Declaration order is index order, so moving a row changes every compiled key index.</remarks>
    public List<BehaviorKeyContent> Keys { get; set; } = [];

    /// <summary>The root, or <see langword="null" /> for an empty tree.</summary>
    public BehaviorNodeContent? Root { get; set; }

    /// <summary>Compiles the keys into a layout.</summary>
    /// <param name="diagnostics">Where a duplicate or colliding name is reported.</param>
    /// <returns>The layout.</returns>
    public BlackboardLayout BuildLayout(ICollection<BehaviorTreeDiagnostic>? diagnostics = null) {
        var builder = new BlackboardLayoutBuilder();

        foreach (var key in Keys) {
            try {
                builder.Add(key.Name, key.Type);
            } catch (Exception error) when (error is InvalidOperationException or ArgumentException) {
                diagnostics?.Add(new(Symbol.Intern(key.Name), error.Message));
            }
        }

        return builder.Build();
    }
}
