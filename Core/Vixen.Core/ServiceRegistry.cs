// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core;

/// <summary>
///     A flat, typed lookup from a service's compile-time type to its single instance. The
///     bootstrapper fills it in by hand and subsystems read from it; that is the whole model.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is not a DI container.</b> There is no constructor injection, no lifetime scope,
///         no auto-wiring, and no reflection — a subsystem takes what it needs as constructor
///         parameters, and whoever constructs it supplies them. The registry exists for the
///         genuinely global handful (graphics device, content manager, input, time) that would
///         otherwise be threaded through every signature. Under NativeAOT the alternative does not
///         work at all, and on a frame budget it costs more than it is worth.
///     </para>
///     <para>
///         The service type is the *static* type argument, not the instance's type, so registering
///         an implementation against its interface is the ordinary case:
///         <c>registry.Add&lt;IGraphicsDevice&gt;(vulkanDevice)</c>.
///     </para>
///     <para>
///         ⚠ <b>A value can be registered too, through <see cref="AddValue{T}" />.</b> The table has
///         always held <c>object</c>, so the <c>where T : class</c> on <see cref="Add{T}" /> was
///         never about what a dependency may be — it is what lets <see cref="Get{T}" /> and
///         <see cref="TryGet{T}" /> answer "not registered" with <see langword="null" />. A value
///         needs a different pair of methods for that reason and for no other, and it is keyed on
///         its static type exactly as a service is. See <see cref="AddValue{T}" /> for what that
///         costs a project that needs two of one type.
///     </para>
///     <para>
///         Reads are lock-free and safe against concurrent writes: a write clones the table and
///         swaps it in, so a reader either sees the old table or the new one, never a torn one.
///         Writes are expected at boot, and are not cheap enough to do per frame.
///     </para>
/// </remarks>
public sealed class ServiceRegistry : IServiceProvider {
    readonly Lock gate = new();

    volatile Dictionary<Type, object> services = [];

    /// <summary>How many services are registered.</summary>
    public int Count => services.Count;

    /// <summary>The registered service types, in no particular order.</summary>
    public IReadOnlyCollection<Type> ServiceTypes => services.Keys;

    /// <summary>Registers <paramref name="service" /> under <typeparamref name="T" />.</summary>
    /// <typeparam name="T">The type callers will ask for.</typeparam>
    /// <param name="service">The instance to hand out.</param>
    /// <exception cref="ArgumentException">A service is already registered under <typeparamref name="T" />.</exception>
    public void Add<T>(T service) where T : class {
        ArgumentNullException.ThrowIfNull(service);

        lock (gate) {
            if (services.ContainsKey(typeof(T))) {
                throw new ArgumentException(
                    $"A service is already registered as {typeof(T)}. Use {nameof(AddOrReplace)} to replace it.",
                    nameof(service)
                );
            }

            services = new(services) { [typeof(T)] = service };
        }
    }

    /// <summary>Registers a value under <typeparamref name="T" />, boxed once.</summary>
    /// <typeparam name="T">The type callers will ask for.</typeparam>
    /// <param name="value">The value to hand out.</param>
    /// <exception cref="ArgumentException">Something is already registered under <typeparamref name="T" />.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>What a system whose dependency is a value asks for.</b> A <c>[GameSystem]</c>'s
    ///         constructor is its dependency list, and the generator already emits
    ///         <c>(T) services[0]</c> for a <c>struct</c> parameter — so an <c>Entity</c>, a handle
    ///         or a small settings struct was declarable and permanently unsatisfiable, because
    ///         nothing could put one in here. This is the way in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One per type, exactly as a service is.</b> The key is <c>typeof(T)</c>, so a
    ///         project that needs two <c>Entity</c>-shaped dependencies gives them distinguishing
    ///         types — <c>readonly record struct Intruder(Entity Entity)</c> — the same answer as
    ///         for two services of one class. Registering a bare framework primitive is legal and
    ///         is rarely what anyone means: <c>typeof(int)</c> is one slot for the whole process.
    ///     </para>
    ///     <para>
    ///         The box is made here and handed out by reference afterwards, so resolving a declared
    ///         system's value parameters allocates nothing.
    ///     </para>
    /// </remarks>
    public void AddValue<T>(T value) where T : struct {
        lock (gate) {
            if (services.ContainsKey(typeof(T))) {
                throw new ArgumentException(
                    $"A service is already registered as {typeof(T)}.",
                    nameof(value)
                );
            }

            services = new(services) { [typeof(T)] = value };
        }
    }

    /// <summary>Registers <paramref name="service" /> unless <typeparamref name="T" /> is taken.</summary>
    /// <typeparam name="T">The type callers will ask for.</typeparam>
    /// <param name="service">The instance to hand out.</param>
    /// <returns><see langword="false" /> if a service was already registered.</returns>
    public bool TryAdd<T>(T service) where T : class {
        ArgumentNullException.ThrowIfNull(service);

        lock (gate) {
            if (services.ContainsKey(typeof(T))) {
                return false;
            }

            services = new(services) { [typeof(T)] = service };
            return true;
        }
    }

    /// <summary>Registers <paramref name="service" />, displacing any current registration.</summary>
    /// <typeparam name="T">The type callers will ask for.</typeparam>
    /// <param name="service">The instance to hand out.</param>
    /// <returns>The service that was registered before, if any.</returns>
    public T? AddOrReplace<T>(T service) where T : class {
        ArgumentNullException.ThrowIfNull(service);

        lock (gate) {
            services.TryGetValue(typeof(T), out var previous);
            services = new(services) { [typeof(T)] = service };
            return (T?)previous;
        }
    }

    /// <summary>Looks up the service registered under <typeparamref name="T" />.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The registered instance.</returns>
    /// <exception cref="ServiceNotFoundException">Nothing is registered under <typeparamref name="T" />.</exception>
    public T Get<T>() where T : class =>
        services.TryGetValue(typeof(T), out var service) ? (T)service : throw new ServiceNotFoundException(typeof(T));

    /// <summary>Looks up the service registered under <typeparamref name="T" />, if there is one.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <param name="service">The registered instance, or <see langword="null" />.</param>
    /// <returns><see langword="true" /> if a service was registered.</returns>
    public bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class {
        if (services.TryGetValue(typeof(T), out var found)) {
            service = (T)found;
            return true;
        }

        service = null;
        return false;
    }

    /// <summary>Looks up the value registered under <typeparamref name="T" />, if there is one.</summary>
    /// <typeparam name="T">The type it was registered as.</typeparam>
    /// <param name="value">The registered value, or <see langword="default" />.</param>
    /// <returns><see langword="true" /> if a value was registered.</returns>
    /// <remarks>
    ///     ⚠ <b>The counterpart of <see cref="TryGet{T}" /> rather than an overload of it.</b>
    ///     <see langword="default" /> is a perfectly good <c>Entity</c> and a perfectly good zero, so
    ///     a value's "not registered" cannot be the value itself — which is the whole reason the
    ///     reference-typed pair could not be widened to cover this.
    /// </remarks>
    public bool TryGetValue<T>(out T value) where T : struct {
        if (services.TryGetValue(typeof(T), out var found)) {
            value = (T) found;

            return true;
        }

        value = default;

        return false;
    }

    /// <summary>Looks up the service registered under <typeparamref name="T" />, or null.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The registered instance, or <see langword="null" />.</returns>
    public T? GetOrDefault<T>() where T : class =>
        services.TryGetValue(typeof(T), out var service) ? (T)service : null;

    /// <summary>Whether anything is registered under <typeparamref name="T" />.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns><see langword="true" /> if a service is registered.</returns>
    public bool Contains<T>() where T : class => services.ContainsKey(typeof(T));

    /// <summary>Unregisters <typeparamref name="T" />. Does not dispose the instance.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns><see langword="true" /> if something was removed.</returns>
    public bool Remove<T>() where T : class {
        lock (gate) {
            if (!services.ContainsKey(typeof(T))) {
                return false;
            }

            var replacement = new Dictionary<Type, object>(services);
            replacement.Remove(typeof(T));
            services = replacement;
            return true;
        }
    }

    /// <summary>Unregisters everything. Does not dispose the instances.</summary>
    public void Clear() {
        lock (gate) {
            services = [];
        }
    }

    /// <summary>
    ///     <see cref="IServiceProvider" /> support, so BCL and third-party code that expects the
    ///     standard interface can read the registry. Vixen code uses <see cref="Get{T}" />.
    /// </summary>
    /// <param name="serviceType">The service type.</param>
    /// <returns>The registered instance, or <see langword="null" />.</returns>
    object? IServiceProvider.GetService(Type serviceType) {
        ArgumentNullException.ThrowIfNull(serviceType);
        return services.GetValueOrDefault(serviceType);
    }
}
