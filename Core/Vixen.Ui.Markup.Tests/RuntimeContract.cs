// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Markup.Tests;

/// <summary>Components the emitter fixtures instantiate, so a tag has to resolve to a real type.</summary>
/// <remarks>
///     Source rather than types in this assembly, because it is compiled <i>with</i> the generated
///     file. A tag that names a component and an attribute that names a parameter both become
///     ordinary C# identifiers in the output, and the whole point is that Roslyn resolves them the
///     way it resolves any other — against types in the compilation, not against a table here.
/// </remarks>
static class RuntimeContract {
    public const string Components = """
                                     using Vixen.Ui;
                                     using Vixen.Ui.Composition;

                                     public enum DialMode { Slow, Fast }

                                     // An element rather than a component, because a capitalised tag
                                     // may name either and a control is where properties that are
                                     // not strings actually live.
                                     public class Dial : UiElement {
                                         protected override string TagName => "dial";

                                         public DialMode Mode { get; set; }
                                         public float Ratio { get; set; }
                                         public int Steps { get; set; }
                                         public bool Loud { get; set; }
                                         public string? Caption { get; set; }
                                     }

                                     // What a `Control` is, in the one respect `class` cares about:
                                     // it names its own classes in `OnCreated`, before any markup
                                     // attribute is applied. The real one gives itself
                                     // `variant-default` and `size-md` there. Referencing
                                     // Vixen.Ui.Controls from here would buy a heavier test project
                                     // and no more coverage than the two AddClass calls.
                                     public class Gauge : UiElement {
                                         protected override string TagName => "gauge";

                                         protected override void OnCreated() {
                                             base.OnCreated();
                                             AddClass("variant-default");
                                             AddClass("size-md");
                                         }
                                     }

                                     public class Callout : Component {
                                         public string Kind { get; set; } = "";
                                         protected override void Build(BuildContext ctx) => ctx.Element(null, "callout-body");
                                     }

                                     // What an `@inherits` file names. An ordinary element with the
                                     // two hooks the generated scaffold overrides, so the test can
                                     // see that both are chained rather than replaced.
                                     //
                                     // ⚠ Not `Gauge` above, which answers a different question. That
                                     // one exists to be a control that names its own classes, so a
                                     // `class` attribute has something to clobber; this one exists
                                     // to be *derived from*, and counts the calls it received. A
                                     // fixture doing both would fail one of them for the other's
                                     // reason.
                                     public class Panel : UiElement {
                                         protected override string TagName => "panel";

                                         public int Creations { get; private set; }
                                         public int Removals { get; private set; }

                                         protected override void OnCreated() {
                                             base.OnCreated();
                                             Creations++;
                                         }

                                         protected override void OnRemoved() {
                                             Removals++;
                                             base.OnRemoved();
                                         }
                                     }

                                     // What a control that positions its own parts is, in the one
                                     // respect a `style` attribute cares about: it writes inline
                                     // declarations of its own, and a `style` that treated the
                                     // attribute as the element's whole inline set would delete
                                     // them. The real ones are a DataGrid row's `top` and a
                                     // DockingHost pane's `flex-grow`.
                                     //
                                     // ⚠ Not `Gauge`, which answers the same question about
                                     // classes. Two claims in one fixture means each failure is
                                     // reported as the other's.
                                     public class Marker : UiElement {
                                         protected override string TagName => "marker";

                                         protected override void OnCreated() {
                                             base.OnCreated();
                                             SetStyle("top", "5px");
                                         }
                                     }

                                     // What a control is, in the one respect a `change:` binding
                                     // cares about: a property the registry knows by name, which
                                     // raises `PropertyChanged` when it actually changes. That is
                                     // all `bind:` and `change:` ever ask of a control, and it is
                                     // why neither needs an entry per control anywhere.
                                     //
                                     // ⚠ Hand-written because this source is compiled without the
                                     // [UiProperty] generator, and written to match what that
                                     // generator emits — the empty static constructor included.
                                     // Without one the class is beforefieldinit and the CLR may
                                     // defer the registration below past the first instance, which
                                     // is exactly the bug `UiPropertyTests` now guards.
                                     public class Fader : UiElement {
                                         protected override string TagName => "fader";

                                         public static readonly UiPropertyKey LevelProperty =
                                             UiPropertyRegistry.Register(
                                                 "Level",
                                                 typeof(Fader),
                                                 typeof(int),
                                                 false,
                                                 static element => ((Fader) element).Level,
                                                 static (element, value) => ((Fader) element).Level = (int) value!
                                             );

                                         static Fader() {
                                         }

                                         int level;

                                         public int Level {
                                             get => level;
                                             set {
                                                 var previous = level;
                                                 level = value;

                                                 if (previous != value) {
                                                     RaisePropertyChanged(LevelProperty);
                                                 }
                                             }
                                         }
                                     }

                                     // ⚠ **`sealed`, and fed by a method.** This is the panel
                                     // ledger's sixth shape reduced to a fixture: `InspectorView`
                                     // is fed by `Inspect(descriptor, provider, targets)` and
                                     // `ScrollView` is wanted under the tag a stylesheet names,
                                     // both are sealed, and the escape every other gap in this
                                     // language settled on — a four-line subclass exposing the call
                                     // as a property — is exactly what `sealed` refuses.
                                     //
                                     // `sealed` is not incidental here. A fixture that could be
                                     // derived from would let a test pass by writing the subclass
                                     // the real panels cannot write, which is the claim under test.
                                     public sealed class Roster : UiElement {
                                         protected override string TagName => "roster";

                                         public int Inspections { get; private set; }

                                         public void Inspect(string subject, int depth) {
                                             Inspections++;
                                             Text = subject + ":" + depth;
                                         }
                                     }

                                     public class Label : Component {
                                         public string Title { get; set; } = "";
                                         public int Step { get; set; }
                                         protected override void Build(BuildContext ctx) => ctx.Text(null, Title);
                                     }
                                     """;
}
