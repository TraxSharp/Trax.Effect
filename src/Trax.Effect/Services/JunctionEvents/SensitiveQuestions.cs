using System.Collections.Concurrent;
using System.Reflection;
using Trax.Core.Decisions;
using Trax.Effect.Attributes;

namespace Trax.Effect.Services.JunctionEvents;

/// <summary>
/// The question keys whose answers junction events and <c>trax.junction_run</c> leave out: the keys
/// of every enum or marker type marked <see cref="TraxSensitiveAttribute"/>.
/// </summary>
/// <remarks>
/// A decision reaches the observer as a key, not a type, so the marked types are found by scanning
/// the loaded assemblies that reference Trax.Effect (the only ones that can carry the attribute)
/// once, and each assembly loaded afterwards as it loads. Two types can share a key
/// (<c>QuestionKey.For</c> drops the namespace), and when either is marked the answer is withheld
/// for both: a key that might be sensitive is treated as sensitive.
/// </remarks>
internal static class SensitiveQuestions
{
    private static readonly string EffectAssembly = typeof(TraxSensitiveAttribute)
        .Assembly.GetName()
        .Name!;

    private static readonly ConcurrentDictionary<string, byte> Keys = new(StringComparer.Ordinal);

    private static readonly Lazy<bool> Scanned = new(ScanLoaded, isThreadSafe: true);

    /// <summary>Whether the answer to the question with this key must be withheld.</summary>
    public static bool IsSensitive(string key)
    {
        _ = Scanned.Value;
        return Keys.ContainsKey(key);
    }

    /// <summary>Whether the answer to a question about <paramref name="type"/> must be withheld.</summary>
    public static bool IsSensitive(Type type) =>
        type.IsDefined(typeof(TraxSensitiveAttribute), inherit: false)
        || IsSensitive(QuestionKey.For(type));

    private static bool ScanLoaded()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, args) => Scan(args.LoadedAssembly);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            Scan(assembly);

        return true;
    }

    private static void Scan(Assembly assembly)
    {
        try
        {
            if (assembly.IsDynamic)
                return;

            var name = assembly.GetName().Name;
            if (
                name != EffectAssembly
                && !assembly.GetReferencedAssemblies().Any(r => r.Name == EffectAssembly)
            )
                return;

            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException partial)
            {
                types = partial.Types;
            }

            foreach (var type in types)
                if (
                    type is not null
                    && type.IsDefined(typeof(TraxSensitiveAttribute), inherit: false)
                )
                    Keys.TryAdd(QuestionKey.For(type), 0);
        }
        catch
        {
            // An assembly that cannot be inspected cannot carry a mark this process could read.
        }
    }
}
