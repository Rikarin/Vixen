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
