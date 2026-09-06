// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Reactive;

namespace Vixen.Samples.HelloUi;

/// <summary>What the shell is looking at: one object, made of signals.</summary>
/// <remarks>
///     <para>
///         <b>A signal is the whole of the reactivity story and there is nothing else to learn.</b>
///         A <c>.vxml</c> binding is an effect; an effect records what it read while it ran; assigning
///         to a signal re-runs exactly the effects that read it, which assign exactly the properties
///         they were written for. There is no render function to call again, no virtual DOM to diff
///         and nothing to invalidate by hand.
///     </para>
///     <para>
///         ⚠ <b>Do not reach for a revision counter.</b> A <c>Signal&lt;int&gt;</c> bumped by hand to
///         make markup update is manual invalidation wearing a signal's clothes: one bump re-runs
///         every effect in the panel, and a forgotten bump is silent staleness. If a model is not
///         reactive, make its state signal-backed — additively, as below — rather than adding
///         something to poke.
///     </para>
///     <para>
///         ⚠ <b>One model handed down, rather than a value per parameter.</b> A panel given a name
///         and a number would need re-parameterising every time either changed; given the model,
///         every binding follows its own signal and the panel is built once.
///     </para>
/// </remarks>
public sealed class ShellModel {
    /// <summary>The object the inspector inspects.</summary>
    public Material Material { get; } = new();

    /// <summary>Whether shadows are cast, and whether they are received.</summary>
    /// <remarks>
    ///     ⚠ Two checkboxes rather than one, because the second is <i>indeterminate</i> — a state a
    ///     boolean cannot hold and the one worth having a sample for. See <c>Gallery.vxml</c>.
    /// </remarks>
    public Signal<bool> CastsShadows { get; } = new(true);

    /// <summary>Whether the surface draws as wireframe.</summary>
    public Signal<bool> Wireframe { get; } = new(false);

    /// <summary>Which shading quality is chosen.</summary>
    public Signal<string?> Quality { get; } = new("medium");

    /// <summary>What the material is called, as the field has it.</summary>
    public Signal<string?> Name { get; } = new("Standard Material");

    /// <summary>What the asset filter says.</summary>
    public Signal<string?> Filter { get; } = new("");

    /// <summary>What the secure field holds.</summary>
    /// <remarks>
    ///     ⚠ <b>A plain signal of a plain string, which is the honest shape.</b> A
    ///     <c>SecureTextBox</c> masks what it <i>draws</i>; the value is the value, and nothing in
    ///     the framework pretends a managed string can be kept out of the heap. The sample never
    ///     prints it — see <c>Program.Report</c>, which prints the docking arrangement and nothing
    ///     else.
    /// </remarks>
    public Signal<string?> Secret { get; } = new("");

    /// <summary>How many samples the shading takes.</summary>
    public Signal<double> Samples { get; } = new(8d);

    /// <summary>How many copies the stepper's arrows are counting.</summary>
    /// <remarks>
    ///     ⚠ <b>An <c>int</c>, and it used to be a <c>double</c> because <c>bind:</c> is exact.</b>
    ///     The note that stood here said a <c>Signal&lt;int&gt;</c> would refuse at build with both
    ///     type names, which is true of <c>bind:</c> and true of nothing else: the converter seam
    ///     #663 asks for is the pair `Number="@…"` in and `change:Number="@(n => …)"` out, which the
    ///     editor writes twenty-six times and which puts the narrowing where a reader can see it.
    ///     So the model says what it means and the panel says where the cast is. <c>Samples</c>
    ///     above stays a <c>double</c> bound with <c>bind:</c>, so the gallery shows both.
    /// </remarks>
    public Signal<int> Copies { get; } = new(1);

    /// <summary>Which blend mode is chosen.</summary>
    public Signal<string?> Blend { get; } = new("opaque");

    /// <summary>Where the single-value slider sits.</summary>
    public Signal<float> Detail { get; } = new(0.35f);

    /// <summary>A progress bar that moves by itself, so that a still frame is not the only frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Driven from <c>UiDocument.Ticked</c> rather than from the frame loop.</b> Nothing in
    ///     <c>Vixen.Ui</c> knows what time it is except through the clock the host hands the document,
    ///     and a panel that needed the host to call it would be a panel that cannot be mounted in a
    ///     test. See <c>Gallery.vxml</c>, which subscribes.
    /// </remarks>
    public Signal<float> Progress { get; } = new(0f);

    /// <summary>Where the spinner is in its turn, from zero to one.</summary>
    public Signal<float> Phase { get; } = new(0f);

    /// <summary>How the shell says something happened.</summary>
    /// <remarks>
    ///     ⚠ <b>An action on the model rather than an event a panel raises, and the difference is
    ///     what the panels then have to know about each other.</b> A gallery button that reached for
    ///     the toast host would need one; given this, it knows it can report and nothing about where
    ///     a report goes. <c>Shell.vxml</c> is the only file that has both halves.
    ///     <para>
    ///         Defaulted to a no-op rather than left null, so a panel mounted on its own in a test
    ///         reports into nothing instead of throwing.
    ///     </para>
    /// </remarks>
    public Action<string, ControlVariant> Notify { get; set; } = static (_, _) => { };

    /// <summary>What the docking arrangement currently is, as a saved layout would hold it.</summary>
    /// <remarks>
    ///     ⚠ <b>A function rather than a value, because the arrangement is the docking host's and the
    ///     docking host is inside the shell.</b> Reading it means asking; a property assigned once
    ///     would be a snapshot of the layout at start-up, which is exactly the layout nobody wants to
    ///     save. <c>Shell.vxml</c> is what sets this, and <c>Program.cs</c> is what calls it on the
    ///     way out — neither of them holding a reference to the other.
    /// </remarks>
    public Func<string> Arrangement { get; set; } = static () => string.Empty;

    /// <summary>The arrangement the shell opens with.</summary>
    /// <remarks>
    ///     A hierarchy down the left at 22%, then the gallery and the inspector splitting what is
    ///     left. This is what an application would load from disk instead; printing it on the way out
    ///     — see <c>Program.cs</c> — is what demonstrates that the round trip exists.
    /// </remarks>
    public static DockLayout DefaultLayout() =>
        new() {
            Root = new DockSplitNode(
                Orientation.Horizontal,
                new DockGroupNode("hierarchy"),
                new DockSplitNode(
                    Orientation.Horizontal,
                    new DockGroupNode("controls"),
                    new DockGroupNode("inspector"),
                    0.65f
                ),
                0.22f
            )
        };
}
