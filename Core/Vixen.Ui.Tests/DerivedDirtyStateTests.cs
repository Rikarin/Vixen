// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A document that works out for itself whether it is dirty, which is what an application does.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every other test of this model calls <c>MarkDirty</c> by hand, and no application
///         does that.</b> A real document compares what it holds with what it last wrote, so editing
///         a field back to the saved value cleans it again — and the comparison lives in an effect,
///         which means <see cref="EditableDocument.MarkDirty" /> is a signal being written from
///         inside the reactive flush that <c>DocumentCommands.Install</c>'s own effect is reading in.
///         Nothing tested that the graph tolerates that shape, and it is exactly the shape
///         <c>Samples/02-HelloUi</c> now ships.
///     </para>
///     <para>
///         The assertion is always what a <i>menu</i> would see — <c>CommandRoute.CanExecute</c> and
///         the coalesced raise — never the document's own signal, because a predicate that is right
///         while nothing tells the menu to re-ask is the failure this whole seam exists to avoid.
///     </para>
/// </remarks>
public class DerivedDirtyStateTests {
    /// <summary>Dirty is derived: it is whether the value differs from the one Save took.</summary>
    sealed class Sheet : EditableDocument, IDisposable {
        readonly Signal<string> text;
        readonly Effect watch;
        string saved;

        public Sheet(Signal<string> text, EffectScheduler effects) : base("Sheet") {
            this.text = text;
            saved = text.Value;

            watch = new Effect(
                () => {
                    if (string.Equals(this.text.Value, saved, StringComparison.Ordinal)) {
                        MarkClean();
                    } else {
                        MarkDirty();
                    }
                },
                effects
            );
        }

        public void Dispose() => watch.Dispose();

        protected override bool OnSave() {
            saved = text.Value;

            return true;
        }

        protected override bool OnRevert() {
            text.Value = saved;

            return true;
        }
    }

    static UiDocument Laid() {
        var document = new UiDocument(400f, 300f);

        document.Load("root { width: 400px; height: 300px; }");

        return document;
    }

    [Fact]
    public void Save_goes_live_on_the_first_edit_and_grey_again_when_it_is_undone_by_hand() {
        using var document = Laid();

        var text = new Signal<string>("hello");
        using var sheet = new Sheet(text, document.Effects);

        document.Root.HostedDocument = sheet;
        DocumentCommands.Install(document.Root);

        var raised = 0;
        document.CommandsInvalidated += _ => raised++;

        var clock = TimeSpan.Zero;

        void Frame() {
            clock += TimeSpan.FromMilliseconds(16);
            document.Update();
            document.Tick(clock);
        }

        Frame();
        Assert.False(CommandRoute.CanExecute(document, DocumentCommands.Save));

        var settled = raised;

        text.Value = "hello there";
        Frame();

        Assert.True(CommandRoute.CanExecute(document, DocumentCommands.Save));
        Assert.True(CommandRoute.CanExecute(document, DocumentCommands.Revert));
        Assert.Equal(settled + 1, raised);

        // ⚠ Typed back to what was saved, with no Save and no Revert in between. A document that
        // only ever went dirty would leave Save live for the rest of the session — which is the
        // behaviour `MarkDirty()`-by-hand gives and the reason this is derived.
        text.Value = "hello";
        Frame();

        Assert.False(CommandRoute.CanExecute(document, DocumentCommands.Save));
        Assert.Equal(settled + 2, raised);
    }

    [Fact]
    public void Saving_through_the_route_takes_the_snapshot_and_greys_the_item_it_was_invoked_from() {
        using var document = Laid();

        var text = new Signal<string>("hello");
        using var sheet = new Sheet(text, document.Effects);

        document.Root.HostedDocument = sheet;
        DocumentCommands.Install(document.Root);

        text.Value = "edited";
        document.Update();
        document.Tick(TimeSpan.FromMilliseconds(16));

        Assert.True(CommandRoute.Execute(document, DocumentCommands.Save));

        document.Update();
        document.Tick(TimeSpan.FromMilliseconds(32));

        Assert.False(sheet.IsDirty.Value);
        Assert.False(CommandRoute.CanExecute(document, DocumentCommands.Save));

        // And the effect agrees rather than being overruled: the next edit dirties it again, which
        // it could not do if `Save` had left the watch comparing against the old snapshot.
        text.Value = "edited twice";
        document.Update();
        document.Tick(TimeSpan.FromMilliseconds(48));

        Assert.True(CommandRoute.CanExecute(document, DocumentCommands.Save));
    }

    [Fact]
    public void Reverting_through_the_route_puts_the_value_back_and_the_watch_cleans_it() {
        using var document = Laid();

        var text = new Signal<string>("hello");
        using var sheet = new Sheet(text, document.Effects);

        document.Root.HostedDocument = sheet;
        DocumentCommands.Install(document.Root);

        text.Value = "edited";
        document.Update();
        document.Tick(TimeSpan.FromMilliseconds(16));

        Assert.True(CommandRoute.Execute(document, DocumentCommands.Revert));

        Assert.Equal("hello", text.Value);

        document.Update();
        document.Tick(TimeSpan.FromMilliseconds(32));

        Assert.False(CommandRoute.CanExecute(document, DocumentCommands.Revert));
    }
}
