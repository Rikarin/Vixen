// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ai.Tests;

public class BlackboardLayoutTests {
    [Fact]
    public void KeysAreIndexedInDeclarationOrder() {
        var layout = new BlackboardLayoutBuilder()
            .Add("target", BlackboardValueType.Entity)
            .Add("alert", BlackboardValueType.Bool)
            .Add("lastSeen", BlackboardValueType.Vector3)
            .Build();

        Assert.Equal(3, layout.Count);
        Assert.Equal(0, layout.Key("target").Index);
        Assert.Equal(1, layout.Key("alert").Index);
        Assert.Equal(2, layout.Key("lastSeen").Index);
    }

    [Fact]
    public void EveryValueTypeIsLaidOutAlignedAndTwelveBytesAtWorst() {
        var builder = new BlackboardLayoutBuilder();

        foreach (var type in Enum.GetValues<BlackboardValueType>()) {
            builder.Add(type.ToString(), type);
        }

        var layout = builder.Build();

        foreach (var key in layout.Keys) {
            Assert.True(key.Size is > 0 and <= 12, $"{key.Name} is {key.Size} bytes.");
            Assert.Equal(0, key.Offset % BlackboardLayoutBuilder.AlignmentOf(key.Type));
        }
    }

    [Fact]
    public void BoolsCostOneByte() {
        var layout = new BlackboardLayoutBuilder()
            .Add("a", BlackboardValueType.Bool)
            .Add("b", BlackboardValueType.Bool)
            .Add("c", BlackboardValueType.Bool)
            .Build();

        Assert.Equal(3, layout.Size);
    }

    [Fact]
    public void ADuplicateNameIsRefused() {
        var builder = new BlackboardLayoutBuilder().Add("target", BlackboardValueType.Entity);

        var error = Assert.Throws<InvalidOperationException>(
            () => builder.Add("target", BlackboardValueType.Float)
        );

        Assert.Contains("target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingKeyIsAnErrorNamingIt() {
        var layout = new BlackboardLayoutBuilder().Add("target", BlackboardValueType.Entity).Build();

        Assert.False(layout.TryGetKey(Symbol.Intern("nothing"), out var missing));
        Assert.False(missing.IsValid);
        Assert.Throws<KeyNotFoundException>(() => layout.Key("nothing"));
    }

    [Fact]
    public void TheEmptyLayoutIsUsable() {
        var board = new Blackboard(BlackboardLayout.Empty);

        Assert.Equal(0, board.Layout.Count);
        Assert.Equal(0u, board.Version);
    }
}

public class BlackboardTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("flag", BlackboardValueType.Bool)
        .Add("count", BlackboardValueType.Int)
        .Add("distance", BlackboardValueType.Float)
        .Add("position", BlackboardValueType.Vector3)
        .Add("target", BlackboardValueType.Entity)
        .Add("state", BlackboardValueType.Symbol)
        .Build();

    [Fact]
    public void EveryTypeRoundTrips() {
        var board = new Blackboard(Layout);

        board.SetBool(Layout.Key("flag"), true);
        board.SetInt(Layout.Key("count"), -17);
        board.SetFloat(Layout.Key("distance"), 4.25f);
        board.SetVector3(Layout.Key("position"), new(1f, -2f, 3f));
        board.SetEntity(Layout.Key("target"), new(7, 3, 1));
        board.SetSymbol(Layout.Key("state"), Symbol.Intern("hunting"));

        Assert.True(board.GetBool(Layout.Key("flag")));
        Assert.Equal(-17, board.GetInt(Layout.Key("count")));
        Assert.Equal(4.25f, board.GetFloat(Layout.Key("distance")));
        Assert.Equal(new Vector3(1f, -2f, 3f), board.GetVector3(Layout.Key("position")));
        Assert.Equal(new Entity(7, 3, 1), board.GetEntity(Layout.Key("target")));
        Assert.Equal(Symbol.Intern("hunting"), board.GetSymbol(Layout.Key("state")));
    }

    /// <summary>
    ///     Set-ness is independent of value — the property that makes <c>Is Set</c> answerable at all,
    ///     since <c>false</c>, <c>0</c>, the zero vector and the null entity are values somebody means.
    /// </summary>
    [Fact]
    public void SetnessIsIndependentOfValue() {
        var board = new Blackboard(Layout);

        Assert.False(board.IsSet(Layout.Key("flag")));
        Assert.False(board.IsSet(Layout.Key("target")));

        board.SetBool(Layout.Key("flag"), false);
        board.SetEntity(Layout.Key("target"), Entity.Null);
        board.SetInt(Layout.Key("count"), 0);

        Assert.True(board.IsSet(Layout.Key("flag")));
        Assert.True(board.IsSet(Layout.Key("target")));
        Assert.True(board.IsSet(Layout.Key("count")));

        Assert.True(board.Clear(Layout.Key("flag")));
        Assert.False(board.IsSet(Layout.Key("flag")));
        Assert.False(board.Clear(Layout.Key("flag")));
    }

    /// <summary>A version increases if and only if a value changed. Property, over random writes.</summary>
    [Fact]
    public void AVersionIncreasesExactlyWhenSomethingChanged() {
        var board = new Blackboard(Layout);
        var key = Layout.Key("count");
        var random = new Random(20260804);
        var known = 0;
        var isSet = false;

        for (var step = 0; step < 5_000; step++) {
            var before = board.VersionOf(key);
            var whole = board.Version;

            if (random.Next(4) == 0) {
                var cleared = board.Clear(key);

                Assert.Equal(isSet, cleared);
                Assert.Equal(before + (cleared ? 1u : 0u), board.VersionOf(key));
                Assert.Equal(whole + (cleared ? 1u : 0u), board.Version);
                isSet = false;

                continue;
            }

            var value = random.Next(-3, 4);
            var expected = !isSet || value != known;

            Assert.Equal(expected, board.SetInt(key, value));
            Assert.Equal(before + (expected ? 1u : 0u), board.VersionOf(key));
            Assert.Equal(whole + (expected ? 1u : 0u), board.Version);

            known = value;
            isSet = true;
        }
    }

    [Fact]
    public void WritingOneKeyLeavesTheOthersVersionsAlone() {
        var board = new Blackboard(Layout);

        board.SetInt(Layout.Key("count"), 1);
        board.SetInt(Layout.Key("count"), 2);

        Assert.Equal(2u, board.VersionOf(Layout.Key("count")));
        Assert.Equal(0u, board.VersionOf(Layout.Key("distance")));
    }

    /// <summary>An observer fires if and only if it registered, and stops when it is removed.</summary>
    [Fact]
    public void AnObserverFiresExactlyWhenItRegistered() {
        var board = new Blackboard(Layout);
        var watcher = new CountingObserver();
        var handle = board.AddObserver(Layout.Key("count"), watcher);

        Assert.Equal(1, board.ObserverCount(Layout.Key("count")));

        board.SetInt(Layout.Key("count"), 1);
        board.SetFloat(Layout.Key("distance"), 1f);

        Assert.Equal(1, watcher.Calls);

        // A write of the same value is not a change and must not fire.
        board.SetInt(Layout.Key("count"), 1);
        Assert.Equal(1, watcher.Calls);

        board.Clear(Layout.Key("count"));
        Assert.Equal(2, watcher.Calls);

        Assert.True(board.RemoveObserver(handle));
        Assert.False(board.RemoveObserver(handle));

        board.SetInt(Layout.Key("count"), 9);
        Assert.Equal(2, watcher.Calls);
        Assert.Equal(0, board.ObserverCount(Layout.Key("count")));
    }

    [Fact]
    public void ObserversOnOneKeyAllFireAndStayInRegistrationOrder() {
        var board = new Blackboard(Layout);
        var order = new List<int>();

        for (var index = 0; index < 4; index++) {
            var which = index;

            board.AddObserver(Layout.Key("count"), new DelegateObserver(() => order.Add(which)));
        }

        board.SetInt(Layout.Key("count"), 5);

        Assert.Equal(4, order.Count);
        Assert.Equal([3, 2, 1, 0], order);
    }

    /// <summary>An observer that unregisters itself must not saw off the list being walked.</summary>
    [Fact]
    public void AnObserverMayRemoveItselfWhileBeingNotified() {
        var board = new Blackboard(Layout);
        var fired = 0;
        var handles = new BlackboardObserverHandle[3];

        for (var index = 0; index < handles.Length; index++) {
            var slot = index;

            handles[slot] = board.AddObserver(
                Layout.Key("count"),
                new DelegateObserver(() => {
                    fired++;
                    board.RemoveObserver(handles[slot]);
                })
            );
        }

        board.SetInt(Layout.Key("count"), 1);

        Assert.Equal(3, fired);
        Assert.Equal(0, board.ObserverCount(Layout.Key("count")));
    }

    [Fact]
    public void ObserverSlotsAreRecycledSoRegistrationDoesNotAllocateForEver() {
        var board = new Blackboard(Layout);

        for (var pass = 0; pass < 1_000; pass++) {
            var handle = board.AddObserver(Layout.Key("count"), new CountingObserver());

            Assert.True(board.RemoveObserver(handle));
        }

        Assert.Equal(0, board.ObserverCount(Layout.Key("count")));
    }

    [Fact]
    public void ReadingAKeyAsTheWrongTypeIsRefused() {
        var board = new Blackboard(Layout);

        var error = Assert.Throws<InvalidOperationException>(() => board.GetInt(Layout.Key("distance")));

        Assert.Contains("distance", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => board.SetVector3(Layout.Key("count"), Vector3.Zero));
    }

    [Fact]
    public void AKeyFromAnotherLayoutIsRefused() {
        var board = new Blackboard(new BlackboardLayoutBuilder().Add("only", BlackboardValueType.Int).Build());

        Assert.Throws<ArgumentOutOfRangeException>(() => board.GetInt(new BlackboardKey(4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => board.IsSet(BlackboardKey.Invalid));
    }

    [Fact]
    public void ClearingZeroesTheBytesSoAStaleValueCannotBeRead() {
        var board = new Blackboard(Layout);

        board.SetEntity(Layout.Key("target"), new(11, 2, 0));
        board.Clear(Layout.Key("target"));

        Assert.Equal(Entity.Null, board.GetEntity(Layout.Key("target")));
    }

    [Fact]
    public void ResetUnsetsEverythingAndNotifies() {
        var board = new Blackboard(Layout);
        var watcher = new CountingObserver();

        board.SetInt(Layout.Key("count"), 3);
        board.SetFloat(Layout.Key("distance"), 1f);
        board.AddObserver(Layout.Key("count"), watcher);
        board.Reset();

        Assert.False(board.IsSet(Layout.Key("count")));
        Assert.False(board.IsSet(Layout.Key("distance")));
        Assert.Equal(1, watcher.Calls);
    }

    sealed class CountingObserver : IBlackboardObserver {
        public int Calls { get; private set; }

        public void OnBlackboardChanged(Blackboard blackboard, BlackboardKey key) => Calls++;
    }

    sealed class DelegateObserver(Action onChanged) : IBlackboardObserver {
        public void OnBlackboardChanged(Blackboard blackboard, BlackboardKey key) => onChanged();
    }
}

public class SharedBlackboardTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("objective", BlackboardValueType.Vector3)
        .Build();

    [Fact]
    public void AWriteOutsideAScopeIsRefused() {
        var shared = new SharedBlackboard(Layout);

        Assert.Throws<InvalidOperationException>(
            () => shared.Values.SetVector3(Layout.Key("objective"), Vector3.One)
        );
    }

    [Fact]
    public void AWriteInsideAScopeIsAllowedAndReadsNeverAre() {
        var shared = new SharedBlackboard(Layout);

        using (shared.BeginWrite()) {
            Assert.True(shared.IsWriting);
            shared.Values.SetVector3(Layout.Key("objective"), Vector3.One);
        }

        Assert.False(shared.IsWriting);
        Assert.Equal(Vector3.One, shared.Values.GetVector3(Layout.Key("objective")));
    }

    [Fact]
    public void TwoScopesAtOnceAreRefused() {
        var shared = new SharedBlackboard(Layout);

        using (shared.BeginWrite()) {
            Assert.Throws<InvalidOperationException>(() => shared.BeginWrite());
        }
    }

    /// <summary>
    ///     ⚠ <b>A dedicated thread, and <c>Task.Run</c> is what this used to use and why it was
    ///     flaky.</b> xUnit runs a test on the thread pool, an <c>await</c> hands that thread back, and
    ///     the pool is then free to schedule the queued work item onto the very thread that opened the
    ///     scope — at which point the owner check is satisfied, nothing throws, and the test fails
    ///     about one run in three for a reason that has nothing to do with the blackboard.
    /// </summary>
    [Fact]
    public void AWriteFromAnotherThreadIsRefusedWhileAScopeIsOpen() {
        var shared = new SharedBlackboard(Layout);

        using (shared.BeginWrite()) {
            Exception? caught = null;
            var thread = new Thread(
                () => {
                    try {
                        shared.Values.SetVector3(Layout.Key("objective"), Vector3.One);
                    } catch (Exception error) {
                        caught = error;
                    }
                }
            );

            thread.Start();
            thread.Join();

            Assert.IsType<InvalidOperationException>(caught);
        }
    }
}
