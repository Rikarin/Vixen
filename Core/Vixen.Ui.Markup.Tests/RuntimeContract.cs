// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Markup.Tests;

/// <summary>
///     The API the emitter's output calls, written out as C# source and compiled beside it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a declaration of what is owed, not a stand-in for it.</b>
///         <c>Vixen.Ui.Composition</c> does not exist yet: building elements for real needs the
///         element tree, the signal graph and a keyed reconciler, and that is <c>Vixen.Ui</c>'s
///         work rather than the markup language's. What compiling against this <i>does</i> prove is
///         the two things the emitter is actually responsible for — that its output is valid C#,
///         and that an error inside it is reported against the <c>.vxml</c> line that caused it.
///     </para>
///     <para>
///         It is deliberately source rather than types in this assembly. A type the test project
///         declared would be indistinguishable from a type the framework shipped, and this one has
///         to stay obviously neither.
///     </para>
/// </remarks>
static class RuntimeContract {
    public const string Source = """
                                 namespace Vixen.Ui.Composition;

                                 public class UiElement { }

                                 public abstract class Component {
                                     public UiElement Root { get; } = new();
                                     public UiElement Content { get; } = new();
                                     protected virtual string? Style => null;
                                     protected virtual bool StyleIsScoped => false;
                                     protected abstract void Build(BuildContext ctx);
                                 }

                                 public sealed class BuildContext {
                                     public UiElement Element(UiElement? parent, string tag) => new();
                                     public T Child<T>(UiElement? parent) where T : Component, new() => new();
                                     public UiElement Text(UiElement? parent, string text) => new();
                                     public UiElement Text(UiElement? parent, System.Func<object?> text) => new();
                                     public void Attribute(UiElement target, string name, string value) { }
                                     public void Bind(UiElement target, string name, System.Func<object?> value) { }
                                     public void Bind(System.Action assign) { }
                                     public void On(UiElement target, string name, System.Action handler, params string[] modifiers) { }
                                     public void On<T>(UiElement target, string name, System.Action<T> handler, params string[] modifiers) { }
                                     public void TwoWay<T>(UiElement target, string name, System.Func<T> get, System.Action<T> set) { }
                                     public void Slot(UiElement? parent, string name) { }
                                     public void Switch(UiElement? parent, System.Func<int> arm, System.Action<BuildContext, UiElement, int> build) { }
                                     public void For<T>(
                                         UiElement? parent,
                                         System.Func<System.Collections.Generic.IEnumerable<T>> items,
                                         System.Func<T, object> key,
                                         System.Action<BuildContext, UiElement, T> build
                                     ) { }
                                 }
                                 """;

    /// <summary>Components the fixtures instantiate, so a tag really does have to resolve to a type.</summary>
    public const string Components = """
                                     using Vixen.Ui.Composition;

                                     public class Callout : Component {
                                         public string Kind { get; set; } = "";
                                         protected override void Build(BuildContext ctx) { }
                                     }

                                     public class Label : Component {
                                         public string Title { get; set; } = "";
                                         public int Step { get; set; }
                                         protected override void Build(BuildContext ctx) { }
                                     }
                                     """;
}
