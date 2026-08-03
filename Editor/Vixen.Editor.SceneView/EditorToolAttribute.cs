// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.SceneView;

/// <summary>Marks a viewport input as a tool the scene pane offers.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D3.</b> The class implements <see cref="IViewportInput" /> and needs a
///         parameterless constructor — it is made by the editor and registered as a
///         <see cref="SceneTool" />.
///     </para>
///     <code language="csharp">
///         [EditorTool("Sculpt", typeof(TerrainComponent))]
///         public sealed class SculptTool : IViewportInput { … }
///     </code>
///     <para>
///         ⚠ <b><see cref="Target" /> is what the tool is <i>for</i>, not what it is allowed to
///         touch.</b> A tool with one appears when something of that type is selected and is put away
///         when it is not, which is the whole reason a pane can offer twenty tools without being a
///         wall of twenty buttons. A tool with none is always offered.
///     </para>
///     <para>
///         ⚠ <b>Read by a scan of the assembly that declared it, and only for a plugin or a project
///         script</b> — see <c>CustomInspectorAttribute</c> for why that is bounded rather than the
///         assembly scanning ADR-002 refuses. In-tree code registers a <see cref="SceneTool" />
///         directly.
///     </para>
/// </remarks>
/// <param name="title">What the tool strip calls it.</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EditorToolAttribute(string title) : Attribute {
    /// <summary>What a person reads on the tool strip.</summary>
    public string Title { get; } = title;

    /// <summary>What has to be selected for it to be offered, or <see langword="null" /> for always.</summary>
    public Type? Target { get; init; }

    /// <summary>The tool's id, or empty to derive one from the title.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth setting for anything a key should be bound to</b>, on <c>EditorMenuAttribute.Id</c>'s
    ///     terms: a derived id changes when the title does, so renaming the tool drops the user's
    ///     binding for it.
    /// </remarks>
    public string Id { get; init; } = string.Empty;

    /// <summary>Which of two tools comes first on the strip; the lower one.</summary>
    public int Order { get; init; }

    /// <summary>Names the tool and what it is for, in one.</summary>
    /// <param name="title">What the tool strip calls it.</param>
    /// <param name="target">What has to be selected for it to be offered.</param>
    /// <remarks>
    ///     Doc 36 § D3 spells it <c>[EditorTool("Sculpt", typeof(TerrainComponent))]</c>, so that is a
    ///     constructor rather than a property somebody has to know to set. The two forms are the same
    ///     thing.
    /// </remarks>
    public EditorToolAttribute(string title, Type target) : this(title) => Target = target;
}
