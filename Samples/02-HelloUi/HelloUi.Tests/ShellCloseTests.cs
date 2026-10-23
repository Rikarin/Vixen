// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Samples.HelloUi.Tests;

/// <summary>The sample's two closes: File ▸ Close puts the material down, and a quit quits (#1509).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The shell installed the window's prompt with a document close as its action</b>, so a quit
///         over a dirty material asked Save / Don't Save / Cancel and then, on Save or Don't Save, reopened
///         an empty material and stayed open: the user answered a question about quitting and nothing
///         quit. #1504 split the prompt; the sample had not followed.
///     </para>
///     <para>
///         ⚠ <b>The quit here retries the request, as <c>UiApplication.Quit</c> does</b> — a counter alone
///         would pass a prompt that asks again on the retry, which is an application that cannot be quit.
///     </para>
/// </remarks>
public sealed class ShellCloseTests : IDisposable {
    readonly UiTest ui = UiTest.Create(1280f, 800f);
    readonly ShellModel model = new();
    readonly Shell shell;

    int quits;
    bool? retried;

    public ShellCloseTests() {
        ControlTheme.Install(ui.Document);
        AdvancedTheme.Install(ui.Document);
        ui.Load(VixenUtilityStyles.Css);

        // What `Program.cs` hands the shell from `Started`: the head's quit, which asks again.
        model.Quit = () => {
            quits++;
            retried = ui.Document.RequestClose(UiCloseReason.Quit);
        };

        shell = new Shell { Model = model };
        BuildContext.BuildInto(shell, ui.Document, ui.Document.Root);
        ui.Frame();
    }

    public void Dispose() => ui.Dispose();

    /// <summary>A quit over a dirty material asks, and on Save or Don't Save quits — once — without reopening anything.</summary>
    /// <remarks>
    ///     ⚠ <b>With the focus in the shell and with no focus at all, and the second was a defect of its own.</b>
    ///     A quit is raised from the focus outwards, or from the document's root when nothing has it — which is
    ///     above the shell's root, where the prompt listened. So with nothing focused a quit over a dirty
    ///     material was never asked about and the edits went with the window.
    /// </remarks>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Answering_a_quit_quits_rather_than_reopening_the_material(bool save, bool focused) {
        var hosted = Assert.IsAssignableFrom<IEditableDocument>(shell.Root.HostedDocument);

        model.Name.Value = "Brushed Steel";
        ui.Frame();
        Assert.True(hosted.IsDirty.Value);

        if (focused) {
            ui.Document.Focus(Descendants(shell.Root).First(element => element is TextBox));
        }

        Assert.False(ui.Document.RequestClose(UiCloseReason.Quit), "a quit over a dirty material was not asked about");
        ui.Frame();

        Press(save ? ControlStrings.DocumentSave.Text : ControlStrings.DocumentDiscard.Text);
        ui.Frame();

        Assert.Same(hosted, shell.Root.HostedDocument);
        Assert.Equal(1, quits);
        Assert.True(retried, "the quit's retry was refused, so the application could not be quit");
        Assert.Equal(!save, hosted.IsDirty.Value);
    }

    /// <summary>Cancel on a quit neither quits nor reopens.</summary>
    [Fact]
    public void Cancelling_a_quit_stays_and_keeps_the_material() {
        var hosted = shell.Root.HostedDocument;

        model.Name.Value = "Brushed Steel";
        ui.Frame();

        Assert.False(ui.Document.RequestClose(UiCloseReason.Quit));
        ui.Frame();

        Press(ControlStrings.DialogCancel.Text);
        ui.Frame();

        Assert.Same(hosted, shell.Root.HostedDocument);
        Assert.Equal(0, quits);
    }

    /// <summary>File ▸ Close still puts the material down and opens an empty one, and is not a quit.</summary>
    [Fact]
    public void Closing_the_material_reopens_it_and_does_not_quit() {
        var hosted = shell.Root.HostedDocument;

        model.Name.Value = "Brushed Steel";
        ui.Frame();

        Assert.False(shell.Root.RequestClose(), "closing a dirty material was not asked about");
        ui.Frame();

        Press(ControlStrings.DocumentDiscard.Text);
        ui.Frame();

        Assert.NotSame(hosted, shell.Root.HostedDocument);
        Assert.Equal(0, quits);
    }

    void Press(string label) {
        var dialog = Descendants(ui.Document.Root).OfType<Dialog>().SingleOrDefault()
            ?? throw new InvalidOperationException("nothing is being asked");

        var button = dialog.Footer.Children.OfType<Button>().FirstOrDefault(one => one.Label == label)
            ?? throw new InvalidOperationException($"the dialog has no '{label}' button");

        button.Activate();
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var below in Descendants(child)) {
                yield return below;
            }
        }
    }
}
