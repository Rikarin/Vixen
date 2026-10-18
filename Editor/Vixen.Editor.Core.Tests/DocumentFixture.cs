// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Core.Tests;

/// <summary>A project with nothing on disk, for the tests that are about the model and not the files.</summary>
/// <remarks>
///     Constructing an <see cref="EditorProject" /> reads nothing — that is <see cref="EditorProject.Open" />
///     — so the document model can be exercised against a path that does not exist, which is what
///     keeps these tests measured in microseconds.
/// </remarks>
public static class ModelFixture {
    /// <summary>A project over a path nobody will touch.</summary>
    /// <returns>The project.</returns>
    public static EditorProject Project() => new(new(Path.Combine(Path.GetTempPath(), "vixen-model-tests")));
}

/// <summary>A document that counts how often it was asked to write itself back.</summary>
public sealed class TestDocument : EditorDocument {
    /// <summary>How many times <see cref="EditorDocument.Save" /> reached the deriving type.</summary>
    public int Saves { get; private set; }

    /// <summary>Opens a document in a project.</summary>
    /// <param name="project">The project.</param>
    /// <param name="title">What the tab says.</param>
    /// <param name="asset">The asset it edits.</param>
    public TestDocument(EditorProject project, string title = "Untitled", AssetId asset = default)
        : base(project, asset, title) {
    }

    /// <inheritdoc />
    protected override void SaveCore() => Saves++;
}

/// <summary>An object with one coalescing value and one that is not, edited the way the editor edits.</summary>
/// <remarks>
///     ⚠ <b>It used to be an <c>EditorObject</c> with two <c>EditorProperty&lt;T&gt;</c>s</b>, and those
///     types had no production user anywhere under <c>Editor/</c> — the stack's merge, seal,
///     transaction and dirty tests were proving the command stack through a path nothing else took
///     (doc 50 § F10, #1282). Each write now goes through <see cref="EditProperty" /> and the
///     <see cref="SetValuesCommand" /> it builds, which is what the inspector, a graph port and a
///     settings row all reach, so the stack is tested through the commands it actually receives.
/// </remarks>
public sealed class Knob {
    float amount;
    string label = "none";

    /// <summary>A value a slider would drag: consecutive edits collapse.</summary>
    public Dial<float> Amount { get; }

    /// <summary>A value a dropdown would set: two edits are two decisions.</summary>
    public Dial<string> Label { get; }

    /// <summary>Creates the object.</summary>
    /// <param name="document">Where its edits are recorded, if anywhere.</param>
    public Knob(EditorDocument? document) {
        Amount = new(this, document, "Amount", static knob => knob.amount, static (knob, value) => knob.amount = value, coalescesEdits: true);
        Label = new(this, document, "Label", static knob => knob.label, static (knob, value) => knob.label = value, coalescesEdits: false);
    }
}

/// <summary>One value on a <see cref="Knob" />, written through the editing pipeline.</summary>
/// <typeparam name="T">What it holds.</typeparam>
public sealed class Dial<T> {
    readonly Knob owner;
    readonly Member member;
    readonly EditProperty property;

    internal Dial(
        Knob owner,
        EditorDocument? document,
        string name,
        Func<Knob, T> read,
        Action<Knob, T> write,
        bool coalescesEdits
    ) {
        this.owner = owner;
        member = new Member(name, read, write, coalescesEdits);
        property = new EditProperty(member, [owner], document);
    }

    /// <summary>What it holds now.</summary>
    public T Value => (T) member.Read(owner)!;

    /// <summary>The same, for the call sites that only look.</summary>
    /// <returns>The value.</returns>
    public T Peek() => Value;

    /// <summary>Changes it the way the inspector would: undoably when there is a document.</summary>
    /// <param name="value">The new value.</param>
    public void Set(T value) => property.Write(value);

    /// <summary>Changes it behind the stack's back, which is what a command's own body does.</summary>
    /// <param name="value">The new value.</param>
    public void Assign(T value) => member.Write(owner, value);

    sealed class Member(string name, Func<Knob, T> read, Action<Knob, T> write, bool coalescesEdits) : IEditMember {
        public string Name => name;

        public string DisplayName => name;

        public Type ValueType => typeof(T);

        public bool CanWrite => true;

        public bool CoalescesEdits => coalescesEdits;

        public bool AreEqual(object? left, object? right) => Equals(left, right);

        public object? Read(object owner) => read((Knob) owner);

        public void Write(object owner, object? value) => write((Knob) owner, (T) value!);

        public IEditorCommand CreateSetCommand(IReadOnlyList<object> targets, object? value, EditorDocument? document) {
            var previous = new object?[targets.Count];

            for (var index = 0; index < targets.Count; index++) {
                previous[index] = Read(targets[index]);
            }

            return new SetValuesCommand(this, targets, previous, value, document);
        }
    }
}
