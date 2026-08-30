using System.Text.RegularExpressions;
using NotificationHub.SharedKernel;

namespace NotificationHub.SecurityArchTests;

/// <summary>
/// A caught exception that leaves as a success leaves a line behind.
/// <para>
/// The rule reads one shape, and the narrowness is the whole point of it. A
/// catch that answers with a failure announces itself: the failure travels the
/// result axis, the caller reads it off <c>IsFailure</c>, and the incident is
/// told wherever the call was made. A catch that answers with a success erases
/// the incident from that axis, and nothing downstream has anything left to
/// read. That is the one shape whose witness cannot come from anywhere else,
/// so it is the one shape obliged to leave it here.
/// </para>
/// <para>
/// The wider reading, every catch that wraps a trail write logs, was measured
/// and set aside. It reports sixteen blocks that hand a business rule
/// violation back to the caller from inside the catch, which is a refusal the
/// caller sees and acts on rather than an incident nobody can discover, and a
/// gate that calls sixteen non-defects violations teaches its reader to skip
/// it. This predicate is narrower and answers for the shape that hides.
/// </para>
/// <para>
/// Reach and residue, measured and stated rather than left silent. The scan
/// covers the feature slices of the modules and nothing else, so a catch in
/// module infrastructure, in the platform, or in the worker host is outside
/// it. The match stops at the block boundary: a catch that returns a helper
/// which builds the success one method over escapes the rule, and the four
/// blocks that return through such a helper today build a business rule
/// violation, so that residue hides nothing at the time of writing. The
/// witness is recognized by a call on a receiver whose name ends in logger,
/// which is an identifier and nothing stronger, so a receiver renamed at its
/// declaration escapes this rule.
/// </para>
/// </summary>
public sealed partial class SwallowedFailureWitnessTests
{
    /// <summary>
    /// The anchor of the walk. It names no feature folder, because those are
    /// renamed as a module grows and a rule anchored on one of them keeps
    /// passing over an empty set instead of reporting that it lost its subject.
    /// </summary>
    private const string ModulesRoot = "src/Platform.Api/Modules";

    private const string FeaturesFolder = "Features";

    /// <summary>
    /// The single constructor of the success side of the result axis, named
    /// from the type itself so a rename breaks this file at compile time
    /// instead of emptying the pattern below in silence.
    /// </summary>
    private const string SuccessFactory = nameof(Result.Success);

    private const string FilterKeyword = "when";

    /// <summary>
    /// The measured population of the walk: forty-two catch blocks across
    /// twenty-eight files of the feature slices. The floor stands on what the
    /// walk extracted and never on what it matched, because the rule matches
    /// two blocks today and an extractor that broke would match none of them
    /// and report a green that read nothing at all.
    /// </summary>
    private const int ScannedCatchBlocks = 42;

    /// <summary>
    /// A source built to be hostile to the scan: a comment that says the
    /// keyword and opens no block, an exception filter whose property pattern
    /// carries braces of its own before the body starts, a closing brace inside
    /// a string literal, and an interpolation hole carrying a quoted brace.
    /// Each one turns a body into something shorter or longer than the body
    /// when the scan reads text it should not be reading.
    /// </summary>
    private const string ExtractionSample = """
        public void Sample()
        {
            // A comment that says catch and opens no block.
            try
            {
                Work();
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: "23505" })
            {
                FirstWitness();
            }
            catch (Exception exception)
            {
                var closing = "}";
                var text = $"the set: {string.Join("{", values)}";
                SecondWitness();
            }
            catch
            {
                ThirdWitness();
            }
        }
        """;

    [Fact]
    public void Catch_that_answers_with_a_success_declares_a_logger_call()
    {
        (string Path, int Line, string Body)[] blocks = FeatureCatchBlocks();

        // A walk that stopped finding blocks would turn the rule into a green
        // that scanned nothing, and the two blocks it reads today would take
        // any change with them. A block that disappears has to be acknowledged
        // here.
        blocks.Length.ShouldBeGreaterThanOrEqualTo(ScannedCatchBlocks);

        var findings = blocks
            .Where(block => SuccessResult().IsMatch(block.Body))
            .Where(block => !LoggerCall().IsMatch(block.Body))
            .Select(block => $"{block.Path}:{block.Line}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        findings.ShouldBeEmpty();
    }

    /// <summary>
    /// The instrument, read against a source it did not walk. The rule above
    /// matches two blocks out of forty-two, so a scan that lost the shape of a
    /// body would find nothing to report and pass; this is where that failure
    /// becomes visible instead.
    /// </summary>
    [Fact]
    public void Catch_extraction_reads_the_body_past_a_filter_a_comment_and_a_literal()
    {
        (int Line, string Body)[] blocks = CatchBlocks(ExtractionSample, "the extraction sample");

        blocks.Length.ShouldBe(3);

        // The filter opened braces of its own, and the body starts after them.
        blocks[0].Body.ShouldContain("FirstWitness");
        blocks[0].Body.ShouldNotContain("SqlState");

        // A brace written inside a literal ended neither the body nor the hole.
        blocks[1].Body.ShouldContain("SecondWitness");

        blocks[2].Body.ShouldContain("ThirdWitness");

        // The pattern spells the factory as text, so this is what keeps the
        // spelling tied to the axis: the constant breaks the build on a rename,
        // and this line keeps the pattern from surviving one.
        SuccessResult()
            .IsMatch($"return {nameof(Result)}.{SuccessFactory}(value);")
            .ShouldBeTrue("The success pattern no longer matches the factory it is named after.");
    }

    /// <summary>
    /// Every catch block of the feature slices, paired with the file and the
    /// line where its clause opens.
    /// </summary>
    private static (string Path, int Line, string Body)[] FeatureCatchBlocks()
        => [.. FeatureSourceFiles()
            .SelectMany(path => CatchBlocks(File.ReadAllText(path), Relative(path))
                .Select(block => (Path: Relative(path), block.Line, block.Body)))];

    private static string[] FeatureSourceFiles()
        => [.. Directory
            .EnumerateDirectories(ModulesDirectory())
            .Select(module => Path.Combine(module, FeaturesFolder))
            .Where(Directory.Exists)
            .SelectMany(features => Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories))
            .Where(path => !BuildOutput().IsMatch(path))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// The catch blocks of one source, each one the text from the brace that
    /// opens the body to the brace that closes it. Anything the scan cannot
    /// make sense of throws instead of being skipped, because a clause silently
    /// dropped is a block this rule would never read.
    /// </summary>
    private static (int Line, string Body)[] CatchBlocks(string source, string path)
    {
        var code = Mask(source, path);
        List<(int Line, string Body)> blocks = [];

        foreach (Match clause in CatchKeyword().Matches(code))
        {
            var index = SkipWhitespace(code, clause.Index + clause.Length);
            if (index < code.Length && code[index] == '(')
            {
                index = SkipParentheses(code, index, path);
            }

            index = SkipWhitespace(code, index);
            if (StartsWithWord(code, index, FilterKeyword))
            {
                // An exception filter carries a property pattern often enough,
                // and that pattern opens a brace before the body does. Stepping
                // over the filter by parenthesis depth is what keeps the
                // extracted body the body.
                index = SkipParentheses(code, SkipWhitespace(code, index + FilterKeyword.Length), path);
                index = SkipWhitespace(code, index);
            }

            if (index >= code.Length || code[index] != '{')
            {
                throw new InvalidOperationException(
                    $"A catch clause in '{path}' does not open a block.");
            }

            var close = MatchingBrace(code, index, path);
            blocks.Add((LineOf(source, clause.Index), source[index..(close + 1)]));
        }

        return [.. blocks];
    }

    /// <summary>
    /// The source with every comment, string literal and char literal blanked,
    /// so a brace, a quote or the keyword this scan looks for is never read out
    /// of text that is not code. Line breaks survive, which is what keeps the
    /// line of a finding the line of the file.
    /// </summary>
    private static string Mask(string source, string path)
    {
        var masked = source.ToCharArray();
        var index = 0;

        while (index < source.Length)
        {
            var start = index;
            if (source.AsSpan(index).StartsWith("//", StringComparison.Ordinal))
            {
                var line = source.IndexOf('\n', index);
                index = line < 0 ? source.Length : line;
            }
            else if (source.AsSpan(index).StartsWith("/*", StringComparison.Ordinal))
            {
                var close = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = close < 0 ? source.Length : close + 2;
            }
            else if (source[index] is '"' or '@' or '$')
            {
                var close = EndOfStringLiteral(source, index, path);
                if (close < 0)
                {
                    index++;
                    continue;
                }

                index = close;
            }
            else if (source[index] == '\'')
            {
                index = EndOfCharLiteral(source, index, path);
            }
            else
            {
                index++;
                continue;
            }

            Blank(masked, start, index);
        }

        var code = new string(masked);
        GuardBraceBalance(code, path);
        return code;
    }

    /// <summary>
    /// The index just past the string literal that opens at
    /// <paramref name="index"/>, or -1 when the character opens no literal.
    /// The regular, verbatim, interpolated and raw forms all appear in the
    /// scanned tree, and an interpolation hole carries code that quotes again,
    /// which is why the holes are followed rather than assumed to be inert.
    /// </summary>
    private static int EndOfStringLiteral(string source, int index, string path)
    {
        var quote = index;
        while (quote < source.Length && source[quote] is '$' or '@')
        {
            quote++;
        }

        if (quote >= source.Length || source[quote] != '"')
        {
            return -1;
        }

        var verbatim = source.AsSpan(index, quote - index).Contains('@');
        var interpolated = source.AsSpan(index, quote - index).Contains('$');
        var fence = 0;
        while (quote + fence < source.Length && source[quote + fence] == '"')
        {
            fence++;
        }

        return fence >= 3
            ? EndOfRawLiteral(source, quote + fence, fence, path)
            : EndOfQuotedLiteral(source, quote + 1, verbatim, interpolated, path);
    }

    private static int EndOfRawLiteral(string source, int index, int fence, string path)
    {
        var position = index;
        while (position < source.Length)
        {
            if (source[position] != '"')
            {
                position++;
                continue;
            }

            var run = 0;
            while (position + run < source.Length && source[position + run] == '"')
            {
                run++;
            }

            if (run >= fence)
            {
                return position + run;
            }

            position += run;
        }

        throw new InvalidOperationException($"A raw string literal in '{path}' never closes.");
    }

    private static int EndOfQuotedLiteral(string source, int index, bool verbatim, bool interpolated, string path)
    {
        var position = index;
        var hole = 0;

        while (position < source.Length)
        {
            var current = source[position];
            if (current == '\\' && !verbatim)
            {
                position += 2;
                continue;
            }

            if (current == '{' && interpolated)
            {
                var escaped = hole == 0 && source.AsSpan(position).StartsWith("{{", StringComparison.Ordinal);
                position += escaped ? 2 : 1;
                hole += escaped ? 0 : 1;
                continue;
            }

            if (current == '}' && interpolated)
            {
                if (hole == 0)
                {
                    position += source.AsSpan(position).StartsWith("}}", StringComparison.Ordinal) ? 2 : 1;
                    continue;
                }

                hole--;
                position++;
                continue;
            }

            if (current == '"')
            {
                if (hole > 0)
                {
                    var nested = EndOfStringLiteral(source, position, path);
                    position = nested < 0 ? position + 1 : nested;
                    continue;
                }

                if (verbatim && source.AsSpan(position).StartsWith("\"\"", StringComparison.Ordinal))
                {
                    position += 2;
                    continue;
                }

                return position + 1;
            }

            if (current == '\n' && !verbatim)
            {
                throw new InvalidOperationException($"A string literal in '{path}' never closes.");
            }

            position++;
        }

        throw new InvalidOperationException($"A string literal in '{path}' never closes.");
    }

    private static int EndOfCharLiteral(string source, int index, string path)
    {
        var position = index + 1;
        while (position < source.Length)
        {
            if (source[position] == '\\')
            {
                position += 2;
                continue;
            }

            if (source[position] == '\'')
            {
                return position + 1;
            }

            position++;
        }

        throw new InvalidOperationException($"A char literal in '{path}' never closes.");
    }

    private static void Blank(char[] masked, int start, int end)
    {
        for (var index = start; index < end && index < masked.Length; index++)
        {
            if (masked[index] != '\n')
            {
                masked[index] = ' ';
            }
        }
    }

    /// <summary>
    /// The masked source has to read as a file whose braces close, because a
    /// literal the mask misread leaves a brace behind that belongs to nobody,
    /// and a body measured from there is not the body.
    /// </summary>
    private static void GuardBraceBalance(string code, string path)
    {
        var depth = 0;
        foreach (var character in code)
        {
            depth += character switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth < 0)
            {
                throw new InvalidOperationException($"The scan of '{path}' closed a brace it never opened.");
            }
        }

        if (depth != 0)
        {
            throw new InvalidOperationException($"The scan of '{path}' left {depth} brace(s) open.");
        }
    }

    private static int SkipWhitespace(string code, int index)
    {
        var position = index;
        while (position < code.Length && char.IsWhiteSpace(code[position]))
        {
            position++;
        }

        return position;
    }

    private static int SkipParentheses(string code, int index, string path)
    {
        if (index >= code.Length || code[index] != '(')
        {
            throw new InvalidOperationException($"A catch clause in '{path}' opens no parentheses where it must.");
        }

        var depth = 0;
        for (var position = index; position < code.Length; position++)
        {
            depth += code[position] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return position + 1;
            }
        }

        throw new InvalidOperationException($"A catch clause in '{path}' never closes its parentheses.");
    }

    private static int MatchingBrace(string code, int index, string path)
    {
        var depth = 0;
        for (var position = index; position < code.Length; position++)
        {
            depth += code[position] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return position;
            }
        }

        throw new InvalidOperationException($"A catch block in '{path}' never closes.");
    }

    private static bool StartsWithWord(string code, int index, string word)
        => index + word.Length <= code.Length
            && string.CompareOrdinal(code, index, word, 0, word.Length) == 0
            && (index + word.Length == code.Length
                || !(char.IsLetterOrDigit(code[index + word.Length]) || code[index + word.Length] == '_'));

    private static int LineOf(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;

    private static string Relative(string path) => Path.GetRelativePath(FindSolutionRoot(), path);

    private static string ModulesDirectory()
    {
        var directory = Path.Combine(
            FindSolutionRoot(),
            ModulesRoot.Replace('/', Path.DirectorySeparatorChar));

        return Directory.Exists(directory)
            ? directory
            : throw new DirectoryNotFoundException(
                $"The module source anchor '{ModulesRoot}' no longer exists.");
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }

    [GeneratedRegex(@"\bcatch\b")]
    private static partial Regex CatchKeyword();

    [GeneratedRegex(@"\bResult\s*\.\s*Success\b")]
    private static partial Regex SuccessResult();

    [GeneratedRegex(@"\b\w*[Ll]ogger\s*\.\s*\w+\s*\(")]
    private static partial Regex LoggerCall();

    [GeneratedRegex(@"[\\/](bin|obj)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex BuildOutput();
}
