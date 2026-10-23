using System.Collections.Frozen;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Infrastructure.Serialization;

/// <summary>Declares a stable persisted name for a CLR event/message type.</summary>
public sealed record MessageTypeRegistration(Type Type, string Name);

/// <summary>
/// Maps persisted type names to CLR types and back. Names, not CLR type names, are stored, so types can
/// be renamed or moved between namespaces without breaking stored events. Convention: "{module}.{event}.v{n}".
/// </summary>
public sealed class MessageTypeRegistry
{
    private readonly FrozenDictionary<string, Type> typesByName;
    private readonly FrozenDictionary<Type, string> namesByType;

    private MessageTypeRegistry(IReadOnlyCollection<MessageTypeRegistration> registrations)
    {
        typesByName = registrations.ToFrozenDictionary(r => r.Name, r => r.Type, StringComparer.Ordinal);
        namesByType = registrations.ToFrozenDictionary(r => r.Type, r => r.Name);
    }

    public IEnumerable<MessageTypeRegistration> All => namesByType.Select(p => new MessageTypeRegistration(p.Key, p.Value));

    /// <summary>Fails when a name is used for several types or a type is registered under several names.</summary>
    public static Result<MessageTypeRegistry, Error> Create(IEnumerable<MessageTypeRegistration> registrations)
    {
        var all = registrations.Distinct().ToList();

        var duplicateName = all.GroupBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicateName is not null)
        {
            return Error.Conflict(
                "message-types.duplicate-name",
                $"Message type name '{duplicateName.Key}' is registered for several types: {string.Join(", ", duplicateName.Select(r => r.Type.FullName))}.");
        }

        var duplicateType = all.GroupBy(r => r.Type).FirstOrDefault(g => g.Count() > 1);
        return duplicateType is not null
            ? Error.Conflict(
                "message-types.duplicate-type",
                $"Type '{duplicateType.Key.FullName}' is registered under several names: {string.Join(", ", duplicateType.Select(r => r.Name))}.")
            : new MessageTypeRegistry(all);
    }

    public Result<string, Error> NameOf(Type type) =>
        namesByType.TryGetValue(type, out var name)
            ? name
            : Error.NotFound("message-types.unregistered-type", $"Type '{type.FullName}' is not registered as a message type.");

    public Result<Type, Error> TypeOf(string name) =>
        typesByName.TryGetValue(name, out var type)
            ? type
            : Error.NotFound("message-types.unknown-name", $"Message type name '{name}' is not registered.");
}
