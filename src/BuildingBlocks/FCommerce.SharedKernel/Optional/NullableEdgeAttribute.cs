namespace FCommerce.SharedKernel.Optional;

/// <summary>
/// Marks a type that mirrors an external format where values may be missing (configuration bound by the framework,
/// a database row outside a <c>Persistence</c> namespace): its members may be nullable, and code converts them to
/// <see cref="Maybe{T}"/> right where they are read. The architecture tests skip such types; use it only at the edge.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NullableEdgeAttribute : Attribute;
