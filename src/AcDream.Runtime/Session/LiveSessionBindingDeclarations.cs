using System.Reflection;

namespace AcDream.Runtime.Session;

/// <summary>
/// Checks a client's live-session binding record against what that client
/// declares it fills in.
///
/// A client guards most bindings with a nullable part, so a part that came
/// back null quietly leaves a binding empty while the standing declaration --
/// which is read without opening a session -- still says it is filled. A
/// binding the client named a condition for is a configuration and is
/// reported as one; anything else is a defect and says so.
/// </summary>
public static class LiveSessionBindingDeclarations
{
    /// <summary>
    /// Says which declared bindings this particular record left empty.
    /// </summary>
    /// <param name="hostName">Which client built the record.</param>
    /// <param name="what">What kind of record it is, for the line.</param>
    /// <param name="bindings">The record the client really built.</param>
    /// <param name="declared">What that client says it fills in.</param>
    /// <param name="conditional">
    /// The declared bindings that depend on something outside the code, with
    /// the condition in plain terms.
    /// </param>
    /// <param name="warn">Where a line goes; nothing is checked without it.</param>
    public static void ReportDeclaredButUnfilled(
        string hostName,
        string what,
        object bindings,
        IReadOnlySet<string> declared,
        IReadOnlyDictionary<string, string> conditional,
        Action<string>? warn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(conditional);
        if (warn is null)
            return;

        foreach (string member in declared
            .Where(member => IsEmpty(bindings, member))
            .OrderBy(static member => member, StringComparer.Ordinal))
        {
            warn(conditional.TryGetValue(member, out string? condition)
                ? $"session bindings: {what} {member} is unfilled on the "
                    + $"{hostName} client because {condition}; whatever it "
                    + "carries does not happen this session"
                : $"session bindings: {what} {member} is unfilled on the "
                    + $"{hostName} client although the client declares it "
                    + "unconditionally; whatever it carries does not happen");
        }
    }

    /// <summary>
    /// Whether the record holds nothing for that member. A record the member
    /// does not belong to at all is not this check's business: the census is
    /// what catches a declaration naming something that does not exist.
    /// </summary>
    private static bool IsEmpty(object bindings, string member)
    {
        // The bindings are nested -- the session's own, the ones about taking
        // hold of a character and the ones about arriving in the world -- and
        // a declaration names members of all of them, so the lookup follows
        // the nesting one level down.
        if (Find(bindings, member) is not { } property)
        {
            foreach (PropertyInfo outer in Readable(bindings.GetType()))
            {
                if (outer.GetValue(bindings) is not { } nested
                    || nested.GetType().Assembly
                        != bindings.GetType().Assembly)
                {
                    continue;
                }
                if (Find(nested, member) is { } inner)
                    return inner.GetValue(nested) is null;
            }
            return false;
        }
        return property.GetValue(bindings) is null;
    }

    private static PropertyInfo? Find(object record, string member) =>
        record.GetType().GetProperty(
            member,
            BindingFlags.Public | BindingFlags.Instance);

    private static IEnumerable<PropertyInfo> Readable(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(static property => property.CanRead
            && property.GetIndexParameters().Length == 0);
}
