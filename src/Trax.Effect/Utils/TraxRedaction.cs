using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Trax.Effect.Attributes;

namespace Trax.Effect.Utils;

/// <summary>
/// Serializer options that write a <see cref="TraxSensitiveAttribute"/> member as
/// <c>{"_redacted": true}</c>, for every copy of a train's input or output that Trax keeps.
/// </summary>
public static class TraxRedaction
{
    /// <summary>
    /// The property of the object that stands in for a masked value.
    /// </summary>
    public const string MarkerProperty = "_redacted";

    private static readonly ConditionalWeakTable<
        JsonSerializerOptions,
        JsonSerializerOptions
    > Derived = new();

    /// <summary>
    /// Returns options that serialize exactly as <paramref name="options"/> do, except that every
    /// member marked <see cref="TraxSensitiveAttribute"/> is written as <c>{"_redacted": true}</c>.
    /// </summary>
    /// <remarks>
    /// The same instance is returned for the same <paramref name="options"/>, so System.Text.Json's
    /// per-options metadata cache survives across calls. <paramref name="options"/> itself is not
    /// changed. The returned options are for writing: reading a masked value back throws
    /// <see cref="JsonException"/>, because the mask is not the value.
    /// </remarks>
    public static JsonSerializerOptions WithRedaction(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Derived.GetValue(options, Derive);
    }

    /// <summary>
    /// Whether <paramref name="json"/> contains a masked value anywhere, which means it cannot be
    /// read back as the input or output it was written from. Text that is not JSON has none.
    /// </summary>
    public static bool ContainsRedaction(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!json.Contains(MarkerProperty, StringComparison.Ordinal))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            return ContainsMarker(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsMarker(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (
                        property.NameEquals(MarkerProperty)
                        && property.Value.ValueKind == JsonValueKind.True
                    )
                        return true;
                    if (ContainsMarker(property.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsMarker(item))
                        return true;
                return false;
            default:
                return false;
        }
    }

    private static JsonSerializerOptions Derive(JsonSerializerOptions options)
    {
        var resolver = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        var derived = new JsonSerializerOptions(options)
        {
            TypeInfoResolver = resolver.WithAddedModifier(MaskSensitiveMembers),
        };
        derived.MakeReadOnly();
        return derived;
    }

    private static void MaskSensitiveMembers(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var property in typeInfo.Properties)
            if (IsSensitive(typeInfo.Type, property))
                property.CustomConverter = (JsonConverter)
                    Activator.CreateInstance(
                        typeof(RedactingConverter<>).MakeGenericType(property.PropertyType)
                    )!;
    }

    /// <summary>
    /// Marked on the member itself (or a base declaration of it), on the constructor parameter it
    /// binds to (a positional record's parameter), or on an interface member it implements.
    /// </summary>
    private static bool IsSensitive(Type declaringType, JsonPropertyInfo property)
    {
        if (
            property.AttributeProvider is MemberInfo member
            && Attribute.IsDefined(member, typeof(TraxSensitiveAttribute), inherit: true)
        )
            return true;

        if (
            property.AssociatedParameter?.AttributeProvider is ParameterInfo parameter
            && parameter.IsDefined(typeof(TraxSensitiveAttribute), inherit: false)
        )
            return true;

        if (property.AttributeProvider is not MemberInfo clrMember)
            return false;

        // A record's primary constructor parameter, when System.Text.Json did not bind to that
        // constructor (another one was chosen, or the type is only ever written).
        foreach (var constructor in declaringType.GetConstructors())
        foreach (var candidate in constructor.GetParameters())
            if (
                string.Equals(candidate.Name, clrMember.Name, StringComparison.Ordinal)
                && candidate.IsDefined(typeof(TraxSensitiveAttribute), inherit: false)
            )
                return true;

        foreach (var contract in declaringType.GetInterfaces())
        {
            var declared = contract.GetProperty(clrMember.Name);
            if (declared is not null && declared.IsDefined(typeof(TraxSensitiveAttribute), false))
                return true;
        }

        return false;
    }

    private static readonly ConcurrentDictionary<Type, bool> Reaches = new();

    /// <summary>
    /// Whether a value of <paramref name="type"/> can hold a value marked
    /// <see cref="TraxSensitiveAttribute"/>: the type itself is marked, or any instance member,
    /// public or not, of it or of a type it holds (a member's type, an element type, a generic
    /// argument), recursively, is marked as the masking finds a mark (on the member, the record
    /// parameter it binds to, or an interface member it implements). Framework types are not
    /// looked into, only their generic arguments and element types.
    /// </summary>
    internal static bool ReachesSensitiveMember(Type type) =>
        Reaches.GetOrAdd(type, t => Reach(t, []));

    private static bool Reach(Type type, HashSet<Type> seen)
    {
        if (!seen.Add(type))
            return false;

        if (type.IsDefined(typeof(TraxSensitiveAttribute), inherit: true))
            return true;

        if (type.HasElementType && type.GetElementType() is { } element && Reach(element, seen))
            return true;

        if (type.IsGenericType && type.GetGenericArguments().Any(a => Reach(a, seen)))
            return true;

        if (
            type.IsPrimitive
            || type.IsEnum
            || type.IsPointer
            || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
        )
            return false;

        const BindingFlags members =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        for (
            var current = type;
            current is not null && current != typeof(object);
            current = current.BaseType
        )
        {
            var parameters = current
                .GetConstructors(members)
                .SelectMany(c => c.GetParameters())
                .Where(p => p.IsDefined(typeof(TraxSensitiveAttribute), inherit: false))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var property in current.GetProperties(members | BindingFlags.DeclaredOnly))
                if (
                    property.IsDefined(typeof(TraxSensitiveAttribute), inherit: true)
                    || parameters.Contains(property.Name)
                    || current
                        .GetInterfaces()
                        .Select(i => i.GetProperty(property.Name))
                        .Any(p => p?.IsDefined(typeof(TraxSensitiveAttribute), false) == true)
                )
                    return true;

            foreach (var field in current.GetFields(members | BindingFlags.DeclaredOnly))
                if (
                    field.IsDefined(typeof(TraxSensitiveAttribute), inherit: false)
                    || Reach(field.FieldType, seen)
                )
                    return true;
        }

        return false;
    }

    /// <summary>
    /// Writes the stand-in object whatever the value is, and refuses to read one back.
    /// </summary>
    private sealed class RedactingConverter<T> : JsonConverter<T>
    {
        public override T Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) =>
            throw new JsonException(
                "A value masked by [TraxSensitive] cannot be read back; the stored copy does not "
                    + "hold it."
            );

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteBoolean(MarkerProperty, true);
            writer.WriteEndObject();
        }
    }
}
