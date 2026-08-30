using NotificationHub.Api.Modules.TemplateManagement.Domain;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>
/// One argument of one builtin member that decides which culture formats the
/// value, by the position the engine binds it to and by the name a template
/// author can pass it under.
/// </summary>
/// <remarks>
/// Both halves are needed and neither derives from the other: the render-time
/// ban reads the position, because by the time a function is invoked every
/// call form has already collapsed into one positional array; the
/// publication-time check reads the name too, because a named argument is
/// still a name in the syntax tree.
/// </remarks>
internal readonly record struct CultureArgumentSlot(int Index, string Name);

/// <summary>One builtin member of the sandbox that accepts a culture.</summary>
internal sealed record CultureBearingMember(
    string Group,
    string Member,
    IReadOnlyList<CultureArgumentSlot> Slots)
{
    /// <summary>The member as a template author writes it.</summary>
    internal string Path => $"{Group}.{Member}";
}

/// <summary>
/// Every builtin member of the pinned engine that can be handed a culture, and
/// the argument slots that carry it.
/// </summary>
/// <remarks>
/// This lives outside <see cref="ScribanTemplateEngine"/> and not beside the
/// ban that reads it, for a reason that leaves no trace in the source: the
/// engine builds its sandbox surface in a static field initializer declared in
/// its other partial file, and the order of static initializers across two
/// partial files of one class is the order the compiler happened to receive
/// them in. Declared inside the engine, this table was null while the surface
/// was being built, and the whole type failed to initialize. A type of its own
/// is initialized on first use, whoever gets there first.
/// </remarks>
internal static class CultureBearingBuiltins
{
    /// <summary>
    /// Every builtin member of the pinned engine that can be handed a culture,
    /// and the argument slots that carry it. Read off the engine itself rather
    /// than off any document: the surface was enumerated through
    /// <see cref="IScriptFunctionInfo"/> over the whole builtin object, and the
    /// members below are the ones whose parameters name a culture.
    /// <para>
    /// The list holds five members and six slots, and neither number matches
    /// what this module's notes said before it was measured. One member the
    /// notes named, <c>string.to_string</c>, does not exist in this engine at
    /// all; three the notes did not name do. <c>date.parse_to_string</c> is the
    /// one that carries two, one for the culture it reads the text with and one
    /// for the culture it writes the result with, and a ban that covered only
    /// the output slot would leave the input slot open.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyList<CultureBearingMember> Members =
    [
        new("date", "parse", [new CultureArgumentSlot(2, "culture")]),
        new("date", "parse_to_string", [
            new CultureArgumentSlot(2, "output_culture"),
            new CultureArgumentSlot(4, "input_culture"),
        ]),
        new("date", "to_string", [new CultureArgumentSlot(2, "culture")]),
        new("math", "format", [new CultureArgumentSlot(2, "culture")]),
        new("object", "format", [new CultureArgumentSlot(2, "culture")]),
    ];
}

internal sealed partial class ScribanTemplateEngine
{
    /// <summary>
    /// Closes the culture argument of every member above, in place, before the
    /// surface is sealed.
    /// </summary>
    /// <remarks>
    /// The wrapper sits between the engine and the builtin, so it sees the
    /// arguments after the engine has bound them, and that is the whole reason
    /// it can be complete. Measured against the pinned engine, a pipe, a
    /// positional call, a parenthesised call, a named argument, a culture that
    /// arrives in a variable, a literal indexer and an alias of the group all
    /// reach this point as one positional array with the culture in its
    /// declared slot. A check written over the source text, or over the syntax
    /// tree, closes the first few and none of the last ones.
    /// </remarks>
    private static void BanCultureArguments(ScriptObject builtin)
    {
        foreach (CultureBearingMember member in CultureBearingBuiltins.Members)
        {
            if (builtin[member.Group] is not ScriptObject group
                || group[member.Member] is not IScriptCustomFunction inner)
            {
                // An engine that no longer carries the member has moved the
                // surface this ban was measured against. The sentinel test is
                // what reports that; here there is simply nothing to wrap.
                continue;
            }

            group.SetValue(member.Member, new CultureRefusingFunction(inner, member), readOnly: false);
        }
    }

    /// <summary>
    /// The refusal text one banned call earns. It comes from the validation
    /// catalog rather than being spelled again here, because the same content
    /// is refused twice, once at publication and once at render, and an author
    /// who is told two different things about one mistake has to work out that
    /// it is one mistake.
    /// </summary>
    private static string CultureArgumentMessage(string path)
        => TemplateValidation.CultureArgumentMessage(path);

    /// <summary>
    /// Every banned member a source hands a culture to, spelled as the author
    /// wrote it, in source order and without repetition.
    /// </summary>
    /// <remarks>
    /// This is the publication-time half, and it is deliberately weaker than
    /// the render-time half: it reads a call whose target it can name from the
    /// syntax alone, which covers the group-and-member spelling and the literal
    /// indexer, and it cannot follow a group that arrived through a variable.
    /// What it buys for that weakness is timing, because an author finds out
    /// while publishing instead of while a message is going out.
    /// <para>
    /// The walk keeps the nodes it still owes on a stack in the heap, for the
    /// same reason the variable collector does: this runs over source an author
    /// submits, the engine parses a postfix chain in a loop and hands back a
    /// tree as deep as the source allows, and a stack overflow is not catchable
    /// in .NET.
    /// </para>
    /// </remarks>
    private static List<string> CultureArgumentsIn(ScriptNode? root)
    {
        List<string> found = [];
        if (root is null)
        {
            return found;
        }

        // The call on the right of a pipe is reached twice, once through the
        // pipe and once as an ordinary child. Only the first reading knows that
        // the piped value already occupies the first slot.
        HashSet<ScriptNode> piped = new(ReferenceEqualityComparer.Instance);
        Stack<ScriptNode> pending = new();
        pending.Push(root);
        while (pending.TryPop(out ScriptNode? node))
        {
            if (node is ScriptPipeCall { To: ScriptFunctionCall target } && piped.Add(target))
            {
                Record(found, target, pipedArguments: 1);
            }
            else if (node is ScriptFunctionCall call && !piped.Contains(call))
            {
                Record(found, call, pipedArguments: 0);
            }

            for (var index = node.ChildrenCount - 1; index >= 0; index--)
            {
                if (node.GetChildren(index) is { } child)
                {
                    pending.Push(child);
                }
            }
        }

        return found;
    }

    /// <summary>Records the call, once, if it hands a culture to a banned member.</summary>
    private static void Record(List<string> found, ScriptFunctionCall call, int pipedArguments)
    {
        if (CalledMember(call.Target) is not { } path
            || CultureBearingBuiltins.Members.FirstOrDefault(candidate => candidate.Path == path) is not { } banned
            || found.Contains(path, StringComparer.Ordinal))
        {
            return;
        }

        var position = pipedArguments;
        foreach (ScriptExpression argument in call.Arguments ?? [])
        {
            bool occupied;
            if (argument is ScriptNamedArgument named)
            {
                occupied = banned.Slots.Any(
                    slot => string.Equals(slot.Name, named.Name?.Name, StringComparison.Ordinal));
            }
            else
            {
                // Only an argument the author wrote positionally advances the
                // position; a named one binds by name and leaves it where it is.
                var current = position++;
                occupied = banned.Slots.Any(slot => slot.Index == current);
            }

            if (occupied)
            {
                found.Add(path);
                return;
            }
        }
    }

    /// <summary>
    /// The builtin member a call names, when the syntax alone says which one it
    /// is. A group reached through a variable answers nothing here, and is left
    /// to the render-time ban.
    /// </summary>
    private static string? CalledMember(ScriptExpression? target) => target switch
    {
        ScriptMemberExpression { Target: ScriptVariableGlobal group, Member: ScriptVariable member }
            => $"{group.Name}.{member.Name}",
        ScriptIndexerExpression { Target: ScriptVariableGlobal group, Index: ScriptLiteral { Value: string member } }
            => $"{group.Name}.{member}",
        _ => null,
    };

    /// <summary>
    /// One builtin member with its culture argument closed. Everything the
    /// engine asks about the function is answered by the function itself, so
    /// the arity the engine binds against, the parameter names a named argument
    /// resolves through and the return type all stay exactly what they were.
    /// </summary>
    /// <remarks>
    /// The declared arity is left untouched on purpose. Narrowing it would let
    /// the engine refuse the extra argument on its own, but it would refuse it
    /// as an arity mistake, which is the one thing this must not be confused
    /// with: an author who miscounted arguments and an author who forced a
    /// culture need different answers.
    /// </remarks>
    private sealed class CultureRefusingFunction(IScriptCustomFunction inner, CultureBearingMember member)
        : IScriptCustomFunction
    {
        public int RequiredParameterCount => inner.RequiredParameterCount;

        public int ParameterCount => inner.ParameterCount;

        public ScriptVarParamKind VarParamKind => inner.VarParamKind;

        public Type ReturnType => inner.ReturnType;

        public ScriptParameterInfo GetParameterInfo(int index) => inner.GetParameterInfo(index);

        public object? Invoke(
            TemplateContext context,
            ScriptNode? callerContext,
            ScriptArray arguments,
            ScriptBlockStatement? blockStatement)
        {
            Guard(arguments);
            return inner.Invoke(context, callerContext, arguments, blockStatement);
        }

        public ValueTask<object?> InvokeAsync(
            TemplateContext context,
            ScriptNode? callerContext,
            ScriptArray arguments,
            ScriptBlockStatement? blockStatement)
        {
            Guard(arguments);
            return inner.InvokeAsync(context, callerContext, arguments, blockStatement);
        }

        /// <summary>
        /// Refuses a culture that reached the slot, and lets an absent one
        /// through. An omitted argument and one written as <c>null</c> arrive
        /// the same way and mean the same thing, so neither is refused; the
        /// empty string is a culture the author chose and is refused like any
        /// other, because this ban is about who decides the culture and not
        /// about which culture wins.
        /// </summary>
        private void Guard(ScriptArray arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            foreach (CultureArgumentSlot slot in member.Slots)
            {
                if (slot.Index < arguments.Count && arguments[slot.Index] is not null)
                {
                    throw new CultureArgumentRefusedException(member.Path);
                }
            }
        }
    }

    /// <summary>
    /// Carries the refused member out of the engine. The engine wraps whatever
    /// a builtin throws into its own runtime exception and keeps it as the
    /// inner one, which is how the render tells this refusal apart from every
    /// other thing that lands in the same catch.
    /// </summary>
    private sealed class CultureArgumentRefusedException(string path)
        : InvalidOperationException(CultureArgumentMessage(path))
    {
        /// <summary>The member the template handed a culture to.</summary>
        internal string Path { get; } = path;
    }
}
