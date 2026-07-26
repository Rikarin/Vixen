// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     The service registry. The behaviour worth pinning is that the key is the type argument and
///     not the instance's type — that is what lets the bootstrapper register a Vulkan device as
///     <c>IGraphicsDevice</c> and have every consumer see the interface.
/// </summary>
public class ServiceRegistryTests {
    interface IClock {
        int Now { get; }
    }

    sealed class FixedClock(int now) : IClock {
        public int Now => now;
    }

    sealed class OtherService;

    [Fact]
    public void A_service_is_found_under_the_type_it_was_registered_as() {
        var registry = new ServiceRegistry();
        registry.Add<IClock>(new FixedClock(7));

        Assert.Equal(7, registry.Get<IClock>().Now);
        Assert.True(registry.Contains<IClock>());

        // Registered as the interface, so the concrete type is not a key.
        Assert.False(registry.Contains<FixedClock>());
    }

    [Fact]
    public void Asking_for_something_unregistered_names_the_type_that_was_missing() {
        var registry = new ServiceRegistry();

        var exception = Assert.Throws<ServiceNotFoundException>(() => registry.Get<IClock>());
        Assert.Equal(typeof(IClock), exception.ServiceType);
    }

    [Fact]
    public void TryGet_reports_absence_instead_of_throwing() {
        var registry = new ServiceRegistry();

        Assert.False(registry.TryGet<IClock>(out var missing));
        Assert.Null(missing);
        Assert.Null(registry.GetOrDefault<IClock>());

        registry.Add<IClock>(new FixedClock(3));

        Assert.True(registry.TryGet<IClock>(out var found));
        Assert.Equal(3, found.Now);
    }

    [Fact]
    public void Registering_twice_is_an_error_rather_than_a_silent_replacement() {
        var registry = new ServiceRegistry();
        registry.Add<IClock>(new FixedClock(1));

        Assert.Throws<ArgumentException>(() => registry.Add<IClock>(new FixedClock(2)));
        Assert.Equal(1, registry.Get<IClock>().Now);
    }

    [Fact]
    public void TryAdd_yields_to_whatever_is_already_registered() {
        var registry = new ServiceRegistry();

        Assert.True(registry.TryAdd<IClock>(new FixedClock(1)));
        Assert.False(registry.TryAdd<IClock>(new FixedClock(2)));
        Assert.Equal(1, registry.Get<IClock>().Now);
    }

    [Fact]
    public void AddOrReplace_hands_back_what_it_displaced() {
        var registry = new ServiceRegistry();
        var first = new FixedClock(1);

        Assert.Null(registry.AddOrReplace<IClock>(first));
        Assert.Same(first, registry.AddOrReplace<IClock>(new FixedClock(2)));
        Assert.Equal(2, registry.Get<IClock>().Now);
    }

    [Fact]
    public void Removing_reports_whether_there_was_anything_to_remove() {
        var registry = new ServiceRegistry();
        registry.Add<IClock>(new FixedClock(1));

        Assert.True(registry.Remove<IClock>());
        Assert.False(registry.Remove<IClock>());
        Assert.False(registry.Contains<IClock>());
    }

    [Fact]
    public void Count_and_ServiceTypes_describe_what_is_registered() {
        var registry = new ServiceRegistry();
        registry.Add<IClock>(new FixedClock(1));
        registry.Add(new OtherService());

        Assert.Equal(2, registry.Count);
        Assert.Contains(typeof(IClock), registry.ServiceTypes);
        Assert.Contains(typeof(OtherService), registry.ServiceTypes);

        registry.Clear();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void The_registry_answers_IServiceProvider_for_code_that_expects_the_bcl_interface() {
        var registry = new ServiceRegistry();
        var clock = new FixedClock(9);
        registry.Add<IClock>(clock);

        var provider = (IServiceProvider)registry;

        Assert.Same(clock, provider.GetService(typeof(IClock)));
        Assert.Null(provider.GetService(typeof(OtherService)));
    }

    [Fact]
    public void A_null_service_is_rejected_at_the_door() {
        var registry = new ServiceRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Add<IClock>(null!));
    }

    [Fact]
    public async Task Readers_keep_working_while_another_thread_registers() {
        // The copy-on-write table exists for exactly this: a boot thread still wiring services up
        // while a worker reads. A torn Dictionary would surface here as a crash or a lost entry.
        var registry = new ServiceRegistry();
        registry.Add<IClock>(new FixedClock(5));

        var reads = 0;
        var stop = false;
        var reading = new TaskCompletionSource();

        var reader = Task.Run(
            () => {
                while (!Volatile.Read(ref stop)) {
                    Assert.Equal(5, registry.Get<IClock>().Now);
                    if (Interlocked.Increment(ref reads) == 1) {
                        reading.SetResult();
                    }
                }
            },
            TestContext.Current.CancellationToken
        );

        // Wait until the reader is actually running, or the writes below can finish first and the
        // test proves nothing.
        await reading.Task;

        for (var i = 0; i < 1000; i++) {
            registry.AddOrReplace(new OtherService());
        }

        Volatile.Write(ref stop, true);
        await reader;

        Assert.True(Volatile.Read(ref reads) > 0);
    }
}
