// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Reactive;

namespace Vixen.Samples.HelloUi;

/// <summary>The material the inspector edits, seen as something that can be dirty and saved.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The first thing in the repository to be an <c>IEditableDocument</c>, and that is the
///         point of it.</b> <c>DocumentCommands.Install</c>, <c>UiElement.HostedDocument</c> and the
///         whole dirty-state model existed with no production caller at all — the defect this
///         repository meets most often, and the one a sample is the cheapest cure for. Save and
///         Revert in the File menu are bound to <c>document.save</c> and <c>document.revert</c> and to
///         nothing else: the route finds this document because the shell hosts it, and the items grey
///         themselves out from <see cref="IEditableDocument.IsDirty" /> with no enablement rule
///         written anywhere.
///     </para>
///     <para>
///         ⚠ <b>Dirty is <i>derived</i>, not announced.</b> An effect reads every signal the document
///         covers and compares them with the snapshot Save took; editing a field dirties it and
///         editing it back to what was saved cleans it again, which is what every editor does and
///         what a `MarkDirty()` sprinkled through the panels could never do. The first run of the
///         effect compares equal, so nothing has to remember that it is the first.
///     </para>
///     <para>
///         ⚠ <b><see cref="IEditableDocument.Location" /> is null and stays null, because this sample
///         has no file system in it.</b> Null is the framework's way of saying "never saved", which is
///         what makes Save mean Save As — a real application checks it and shows a file dialog first.
///         Saving here takes a snapshot and reverting puts it back, so the round trip is real; what is
///         absent is the disk, and inventing a path would be exactly the lie a sample exists not to
///         tell.
///     </para>
/// </remarks>
sealed class MaterialDocument : EditableDocument, IDisposable {
    readonly ShellModel model;
    readonly Effect watch;
    Snapshot saved;

    public MaterialDocument(ShellModel model, EffectScheduler effects) : base("Standard Material") {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(effects);

        this.model = model;
        saved = Snapshot.Of(model);

        watch = new Effect(
            () => {
                if (Snapshot.Of(model) == saved) {
                    MarkClean();
                } else {
                    MarkDirty();
                }
            },
            effects
        );
    }

    /// <summary>Stops watching. The shell disposes this when its build region goes.</summary>
    public void Dispose() => watch.Dispose();

    /// <inheritdoc />
    protected override bool OnSave() {
        saved = Snapshot.Of(model);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Writing the signals back re-runs the watch, which then finds them equal and cleans the
    ///     document — so the base class's own <c>MarkClean</c> is agreeing with the effect rather
    ///     than overruling it.
    /// </remarks>
    protected override bool OnRevert() {
        saved.ApplyTo(model);
        return true;
    }

    /// <summary>Everything this document considers part of itself, read at one moment.</summary>
    /// <remarks>
    ///     A record struct so that "has it changed" is one equality and not six comparisons that can
    ///     be added to in five places and forgotten in the sixth.
    /// </remarks>
    readonly record struct Snapshot(
        string? Name,
        string? Quality,
        string? Blend,
        bool CastsShadows,
        bool Wireframe,
        float Detail
    ) {
        public static Snapshot Of(ShellModel model) =>
            new(
                model.Name.Value,
                model.Quality.Value,
                model.Blend.Value,
                model.CastsShadows.Value,
                model.Wireframe.Value,
                model.Detail.Value
            );

        public void ApplyTo(ShellModel model) {
            model.Name.Value = Name;
            model.Quality.Value = Quality;
            model.Blend.Value = Blend;
            model.CastsShadows.Value = CastsShadows;
            model.Wireframe.Value = Wireframe;
            model.Detail.Value = Detail;
        }
    }
}
