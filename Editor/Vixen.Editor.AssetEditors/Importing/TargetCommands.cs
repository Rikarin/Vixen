// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>Where an asset appears in a shipped build, as something an inspector can edit.</summary>
/// <remarks>
///     <para>
///         The mutable mirror of <c>AddressableInfo</c>, for the reason
///         <see cref="ImportSettingsDocument" /> gives about mirrors generally.
///     </para>
///     <para>
///         ⚠ <b>Labels are one comma-separated field rather than a list editor.</b> A list drawer is
///         a real gap in <c>Vixen.Editor.Inspector</c> and inventing a bespoke one here would be the
///         second answer to a question the inspector should own. A label is a short identifier and
///         "level1, preload" is a form people already type; the split is on the way out, and empty
///         entries are dropped rather than shipped as a label called nothing.
///     </para>
/// </remarks>
[DataContract("AddressableEdits")]
public sealed class AddressableEdits {
    /// <summary>What the game asks for it by. Empty means it is not shipped by name.</summary>
    [Inspector]
    [Tooltip("What LoadAsync asks for it by. Empty means it is reached through another asset's dependencies.")]
    public string Address { get; set; } = string.Empty;

    /// <summary>Which bundle group it belongs to, or empty to inherit the folder's.</summary>
    [Inspector]
    [Tooltip("Empty inherits from the nearest folder that names a group.")]
    public string Group { get; set; } = string.Empty;

    /// <summary>Labels for bulk loading, comma separated.</summary>
    [Inspector]
    [Tooltip("Comma separated. Labels are not inherited from a folder — a label is a query.")]
    public string Labels { get; set; } = string.Empty;
}

/// <summary>Adding a build target to the override matrix.</summary>
/// <remarks>
///     ⚠ <b>Sealed, and implementing <see cref="IEditorCommand" /> itself, as the other two here
///     are.</b> An abstract base declaring <c>Do</c> would be the shorter arrangement and would put
///     three commands' interface mapping on a type that is not the one the stack calls through —
///     the trap <c>NodeGraphCommand</c> documents. Three short classes cost less than that costs.
/// </remarks>
public sealed class AddTargetCommand : IEditorCommand {
    readonly ImportSettingsDocument document;

    /// <summary>The row it adds. Kept, so an undo and a redo restore the same object.</summary>
    /// <remarks>
    ///     The same rule the node graph's identities follow: a row that came back as a <i>different</i>
    ///     object would leave every control bound to the old one editing something nothing writes.
    /// </remarks>
    public TargetOverride Row { get; }

    /// <inheritdoc />
    public string Name => "Add Target Override";

    /// <summary>Describes adding a target.</summary>
    /// <param name="document">The document.</param>
    /// <param name="target">The build target.</param>
    public AddTargetCommand(ImportSettingsDocument document, string target) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(target);

        this.document = document;
        Row = new(target, document.NewSettings());
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Insert(Row, document.Overrides.Count);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Detach(Row);
        context.Touch(document);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing here merges. Adding a target and marking a member are two decisions a minute
    ///     apart, and collapsing them takes away an undo the author is entitled to.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }
}

/// <summary>Removing a build target from the override matrix.</summary>
public sealed class RemoveTargetCommand : IEditorCommand {
    readonly ImportSettingsDocument document;
    readonly TargetOverride row;
    readonly int index;

    /// <inheritdoc />
    public string Name => "Remove Target Override";

    /// <summary>Describes removing a target.</summary>
    /// <param name="document">The document.</param>
    /// <param name="row">The row.</param>
    /// <param name="index">Where it was, so an undo puts it back there rather than at the end.</param>
    public RemoveTargetCommand(ImportSettingsDocument document, TargetOverride row, int index) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(row);

        this.document = document;
        this.row = row;
        this.index = index;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Detach(row);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Insert(row, index);
        context.Touch(document);
    }

    /// <inheritdoc cref="AddTargetCommand.TryMergeWith" />
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }
}

/// <summary>Turning one member's override on or off for one target.</summary>
public sealed class SetOverriddenCommand : IEditorCommand {
    readonly ImportSettingsDocument document;
    readonly TargetOverride row;
    readonly string member;
    readonly bool overridden;

    /// <inheritdoc />
    public string Name => overridden ? "Override Setting" : "Clear Override";

    /// <summary>Describes marking or unmarking a member.</summary>
    /// <param name="document">The document.</param>
    /// <param name="row">The target's row.</param>
    /// <param name="member">The member's name in source.</param>
    /// <param name="overridden">Whether the target should override it.</param>
    public SetOverriddenCommand(
        ImportSettingsDocument document,
        TargetOverride row,
        string member,
        bool overridden
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrEmpty(member);

        this.document = document;
        this.row = row;
        this.member = member;
        this.overridden = overridden;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(overridden);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(!overridden);
        context.Touch(document);
    }

    /// <inheritdoc cref="AddTargetCommand.TryMergeWith" />
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    void Apply(bool value) {
        if (value) {
            row.Mark(member);
        } else {
            row.Unmark(member);
        }

        document.RaiseOverridesChanged();
    }
}
