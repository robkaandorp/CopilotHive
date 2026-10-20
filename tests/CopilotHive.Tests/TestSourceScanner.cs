namespace CopilotHive.Tests;

/// <summary>
/// Locates the repository root by walking up from the test's base directory until the solution
/// file (or the production source tree) is found — the same discovery the existing source-level
/// tests use.
/// </summary>
internal static class TestRepoRoot
{
    /// <summary>
    /// The repository root directory.
    /// </summary>
    internal static string Find()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null
            && !Directory.GetFiles(dir, "*.slnx").Any()
            && !Directory.Exists(Path.Combine(dir, "src", "CopilotHive")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        Assert.NotNull(dir);
        Assert.True(Directory.Exists(Path.Combine(dir, "src", "CopilotHive")),
            $"Repository root not found from {AppContext.BaseDirectory}");
        return dir;
    }

    /// <summary>
    /// The absolute path of a file inside the test project.
    /// </summary>
    internal static string TestFile(string relativePath)
    {
        var path = Path.Combine(Find(), "tests", "CopilotHive.Tests", relativePath);
        Assert.True(File.Exists(path), $"Test source file not found at {path}");
        return path;
    }
}

/// <summary>
/// Structural analysis over C# source text, used by the source-level assertions that prove a
/// fixture routes its cleanup through the shared helper.
/// <para>
/// Every operation is STRING-AWARE: string literals, char literals, verbatim strings and comments
/// are all recognised, so a structural claim is made about CODE and never about prose that happens
/// to contain braces, slashes or the searched fragment.
/// </para>
/// </summary>
internal static class TestSourceScanner
{
    /// <summary>
    /// Returns <paramref name="source"/> with every character that is NOT code (string literal
    /// content, char literal content and comments) replaced by a space. The result has the SAME
    /// LENGTH as the input, so indices still address the original positions.
    /// </summary>
    internal static string StripNonCode(string source)
    {
        var mask = ComputeCodeMask(source);
        var chars = source.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!mask[i])
                chars[i] = ' ';
        }

        return new string(chars);
    }

    /// <summary>
    /// Marks each character of <paramref name="source"/> that is CODE — that is, neither inside a
    /// string literal, nor inside a char literal, nor inside a comment.
    /// </summary>
    internal static bool[] ComputeCodeMask(string source)
    {
        var mask = new bool[source.Length];
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            // ── Line comment ──────────────────────────────────────────────
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    i++;
                continue;
            }

            // ── Block comment ─────────────────────────────────────────────
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    i++;
                i = Math.Min(i + 2, source.Length);
                continue;
            }

            // ── Verbatim string (@"...") ──────────────────────────────────
            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }
                continue;
            }

            // ── Raw string ("""...""") ────────────────────────────────────
            if (c == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
            {
                i += 3;
                while (i + 2 < source.Length
                    && !(source[i] == '"' && source[i + 1] == '"' && source[i + 2] == '"'))
                {
                    i++;
                }
                i = Math.Min(i + 3, source.Length);
                continue;
            }

            // ── Plain / interpolated string ("...") ───────────────────────
            if (c == '"')
            {
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (source[i] == '"')
                    {
                        i++;
                        break;
                    }

                    i++;
                }
                continue;
            }

            // ── Char literal ('x') ─────────────────────────────────────────
            if (c == '\'')
            {
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (source[i] == '\'')
                    {
                        i++;
                        break;
                    }

                    i++;
                }
                continue;
            }

            mask[i] = true;
            i++;
        }

        return mask;
    }

    /// <summary>
    /// Counts the (non-overlapping) occurrences off <paramref name="fragment"/> in
    /// <paramref name="source"/>.
    /// </summary>
    internal static int CountOccurrences(string source, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }

    /// <summary>
    /// The nesting level of <paramref name="fragment"/>'s first occurrence inside the block it is
    /// given: <c>0</c> when the fragment sits DIRECTLY in that block, <c>1</c> when it sits inside
    /// one nested block (a <c>try</c>, an <c>if</c>, ...).
    /// <para>
    /// The level is measured from the block's OWN opening brace, so a call directly in a method
    /// body is level 0 and a call inside that method's <c>try</c> block is level 1. A
    /// <c>try { ... } catch { ... }</c> around a cleanup call therefore raises the level to 1,
    /// which distinguishes "the call's failure propagates loudly" from "the call's failure is
    /// caught".
    /// </para>
    /// </summary>
    /// <returns>The nesting level, or <c>-1</c> when the fragment does not occur at all.</returns>
    internal static int NestingLevel(string code, string fragment)
    {
        var index = code.IndexOf(fragment, StringComparison.Ordinal);
        if (index < 0)
            return -1;

        // The number of braces OPEN at the fragment's position. The block's own opening brace
        // contributes 1, so the level is that count minus one.
        var depth = 0;
        for (var i = 0; i < index; i++)
        {
            if (code[i] == '{')
                depth++;
            else if (code[i] == '}')
                depth--;
        }

        return depth - 1;
    }

    /// <summary>
    /// True when EVERY code occurrence of <paramref name="fragment"/> lies STRICTLY INSIDE the
    /// brace-delimited TRY block of a <c>try</c> statement that has at least one <c>catch</c>
    /// handler — so each occurrence's failure is actually CAUGHT.
    /// <para>
    /// Membership is decided by the CONCRETE opening and closing brace positions of each try block
    /// (see <see cref="ParseTryStatements"/>), never by nearest keywords plus aggregate brace
    /// depth: a call in a sibling <c>if</c> block that sits between two completed try/catch
    /// statements shares their depth but is in NEITHER try block, and a depth comparison cannot
    /// tell. A call in the try's own <c>catch</c> or <c>finally</c> block is likewise outside the
    /// try block and is not protected by that try.
    /// </para>
    /// <para>
    /// EVERY occurrence is examined, not just one: an earlier UNPROTECTED duplicate (a bare
    /// <c>TestHelpers.ForceDeleteDirectory(...)</c> before the protected call) can throw before the
    /// protected cleanup is ever reached, so a later safe duplicate must not hide it. Occurrences
    /// inside comments or string literals are not code and are ignored; a brace inside a literal or
    /// comment cannot delimit a block, because the ranges are computed on the masked text.
    /// </para>
    /// </summary>
    /// <returns><c>false</c> when the fragment has no code occurrence at all.</returns>
    internal static bool IsInsideTryBlock(string code, string fragment)
    {
        var occurrences = CodeOccurrences(code, fragment);
        if (occurrences.Count == 0)
            return false;

        var statements = ParseTryStatements(StripNonCode(code));
        return occurrences.All(index => statements.Any(statement =>
            statement.Catches.Count > 0
            && IsStrictlyInside(statement.TryBlock, index, fragment.Length)));
    }

    /// <summary>
    /// True when the LAST code occurrence of <paramref name="fragment"/> lies STRICTLY INSIDE the
    /// brace-delimited block of a <c>catch</c> handler. This distinguishes a failure REPORTED from
    /// a catch handler from one merely MENTIONED in the method — including a mention in a sibling
    /// block AFTER the catch has already closed, which shares the catch's depth but is not in it.
    /// <para>
    /// The LAST occurrence is the one examined (the approved semantics): a method that calls the
    /// reporter from BOTH the try block and the catch block is judged on the call that actually
    /// reports. The handler's concrete brace range decides membership (the optional exception
    /// declaration and <c>when</c> filter are skipped to reach the handler's own opening brace).
    /// </para>
    /// </summary>
    /// <returns><c>false</c> when the fragment has no code occurrence at all.</returns>
    internal static bool IsInsideCatchBlock(string code, string fragment)
    {
        var occurrences = CodeOccurrences(code, fragment);
        if (occurrences.Count == 0)
            return false;

        var last = occurrences[^1];
        var statements = ParseTryStatements(StripNonCode(code));
        return statements.Any(statement => statement.Catches.Any(
            catchBlock => IsStrictlyInside(catchBlock, last, fragment.Length)));
    }

    /// <summary>
    /// One <c>try</c> statement: the concrete brace range of its try block and of each of its
    /// <c>catch</c> handlers. A <c>finally</c> block is deliberately NOT a catch handler.
    /// </summary>
    private sealed record TryStatement(BlockRange TryBlock, IReadOnlyList<BlockRange> Catches);

    /// <summary>
    /// The positions of a block's opening and closing brace in the masked text.
    /// </summary>
    private readonly record struct BlockRange(int Open, int Close);

    /// <summary>
    /// True when the span <c>[index, index + length)</c> lies strictly between the block's braces.
    /// </summary>
    private static bool IsStrictlyInside(BlockRange block, int index, int length) =>
        index > block.Open && index + length <= block.Close;

    /// <summary>
    /// Every index at which <paramref name="fragment"/> occurs AS CODE — i.e. whose first character
    /// is neither in a comment nor in a string/char literal.
    /// </summary>
    private static List<int> CodeOccurrences(string code, string fragment)
    {
        var result = new List<int>();
        if (fragment.Length == 0)
            return result;

        var mask = ComputeCodeMask(code);
        var from = 0;
        int found;
        while ((found = code.IndexOf(fragment, from, StringComparison.Ordinal)) >= 0)
        {
            if (mask[found])
                result.Add(found);

            from = found + 1;
        }

        return result;
    }

    /// <summary>
    /// Finds every <c>try</c> statement in the MASKED text and records the concrete brace ranges
    /// of its try block and its catch handlers. Because the text is masked, a brace inside a
    /// string literal, char literal or comment can never open or close a block here.
    /// </summary>
    private static List<TryStatement> ParseTryStatements(string masked)
    {
        var statements = new List<TryStatement>();
        var from = 0;
        int found;
        while ((found = masked.IndexOf("try", from, StringComparison.Ordinal)) >= 0)
        {
            from = found + 1;
            if (!IsStandaloneKeyword(masked, "try", found))
                continue;

            var open = SkipWhitespace(masked, found + 3);
            if (open >= masked.Length || masked[open] != '{')
                continue;

            var close = MatchingClose(masked, open, '{', '}');
            if (close < 0)
                continue;

            var catches = new List<BlockRange>();
            var cursor = SkipWhitespace(masked, close + 1);
            while (IsKeywordAt(masked, cursor, "catch"))
            {
                var k = SkipWhitespace(masked, cursor + "catch".Length);

                // Optional exception declaration: catch (Exception ex)
                if (k < masked.Length && masked[k] == '(')
                {
                    var declarationClose = MatchingClose(masked, k, '(', ')');
                    if (declarationClose < 0)
                        break;

                    k = SkipWhitespace(masked, declarationClose + 1);
                }

                // Optional exception filter: when (condition)
                if (IsKeywordAt(masked, k, "when"))
                {
                    k = SkipWhitespace(masked, k + "when".Length);
                    if (k < masked.Length && masked[k] == '(')
                    {
                        var filterClose = MatchingClose(masked, k, '(', ')');
                        if (filterClose < 0)
                            break;

                        k = SkipWhitespace(masked, filterClose + 1);
                    }
                }

                if (k >= masked.Length || masked[k] != '{')
                    break;

                var catchClose = MatchingClose(masked, k, '{', '}');
                if (catchClose < 0)
                    break;

                catches.Add(new BlockRange(k, catchClose));
                cursor = SkipWhitespace(masked, catchClose + 1);
            }

            statements.Add(new TryStatement(new BlockRange(open, close), catches));
        }

        return statements;
    }

    /// <summary>
    /// The index of the closing delimiter that balances the opening delimiter at
    /// <paramref name="open"/>, or -1 when it is unbalanced. The text must already be masked.
    /// </summary>
    private static int MatchingClose(string masked, int open, char openChar, char closeChar)
    {
        var depth = 0;
        for (var i = open; i < masked.Length; i++)
        {
            if (masked[i] == openChar)
                depth++;
            else if (masked[i] == closeChar)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The index of the first non-whitespace character at or after <paramref name="index"/>.
    /// </summary>
    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    /// <summary>
    /// True when <paramref name="keyword"/> occurs at <paramref name="index"/> as a standalone
    /// token (not part of a longer identifier).
    /// </summary>
    private static bool IsKeywordAt(string text, int index, string keyword) =>
        index >= 0
        && index + keyword.Length <= text.Length
        && string.CompareOrdinal(text, index, keyword, 0, keyword.Length) == 0
        && IsStandaloneKeyword(text, keyword, index);

    /// <summary>
    /// True when the keyword occurrence at <paramref name="index"/> is a standalone token: the
    /// characters around it are not identifier characters.
    /// </summary>
    private static bool IsStandaloneKeyword(string code, string keyword, int index)
    {
        if (index > 0 && IsIdentifierChar(code[index - 1]))
            return false;

        var after = index + keyword.Length;
        return after >= code.Length || !IsIdentifierChar(code[after]);
    }

    /// <summary>
    /// True when the character can be part of a C# identifier.
    /// </summary>
    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Extracts the brace-balanced body of the method whose declaration contains
    /// <paramref name="declarationMarker"/>, keeping the ORIGINAL text — string-literal content
    /// included. Returns null when the marker is absent or the body is unbalanced.
    /// <para>
    /// <see cref="ExtractMethodBlock"/> returns masked text, in which every string literal's
    /// content is replaced by spaces: that is right for structural claims about CODE, but it makes
    /// it impossible to assert on the VALUES a method passes to a call. Brace balancing here is
    /// still driven by the masked text, so a brace inside a string cannot end the block early.
    /// </para>
    /// </summary>
    internal static string? ExtractMethodBlockPreservingLiterals(string source, string declarationMarker)
    {
        var mask = ComputeCodeMask(source);
        var chars = source.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!mask[i])
                chars[i] = ' ';
        }

        var markerIndex = new string(chars).IndexOf(declarationMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        var open = new string(chars).IndexOf('{', markerIndex);
        if (open < 0)
            return null;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (!mask[i])
                continue;

            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[markerIndex..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>
    /// The argument text of the FIRST call to <paramref name="callName"/> inside
    /// <paramref name="code"/> — everything between its parentheses, verbatim (string literals
    /// included). Returns null when the call is absent or its parentheses are unbalanced.
    /// <para>
    /// This is how a test asserts on the VALUES a method actually passes to a call, instead of on
    /// text that happens to appear somewhere in the method (a parameter list, say).
    /// </para>
    /// </summary>
    internal static string? CallArguments(string code, string callName)
    {
        var start = code.IndexOf(callName, StringComparison.Ordinal);
        if (start < 0)
            return null;

        var open = code.IndexOf('(', start + callName.Length);
        if (open < 0)
            return null;

        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '(')
                depth++;
            else if (code[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return code[(open + 1)..i];
            }
        }

        return null;
    }

    /// <summary>
    /// The brace-balanced block of the method that encloses <paramref name="index"/>, or null when
    /// the index is not inside a method body.
    /// </summary>
    internal static string? EnclosingMethodBlock(string code, int index)
    {
        var openers = new Stack<int>();
        for (var i = 0; i < index; i++)
        {
            if (code[i] == '{')
                openers.Push(i);
            else if (code[i] == '}')
            {
                if (openers.Count > 0)
                    openers.Pop();
            }
        }

        while (openers.Count > 0)
        {
            var brace = openers.Pop();
            var declaration = DeclarationLineBefore(code, brace);
            if (declaration is not null && IsMethodDeclaration(declaration))
            {
                var depth = 0;
                for (var i = brace; i < code.Length; i++)
                {
                    if (code[i] == '{')
                        depth++;
                    else if (code[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                            return code[brace..(i + 1)];
                    }
                }

                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The DECLARATION of the method that encloses <paramref name="index"/>: the full signature
    /// text preceding its opening brace, so a caller can inspect the RETURN TYPE rather than the
    /// body. Returns null when the index is not inside a method body.
    /// <para>
    /// This is deliberately NOT <see cref="EnclosingMethodBlock"/>, which returns only the body
    /// (from the opening brace to the closing one): a body is not a declaration, and mistaking the
    /// two makes a body that merely MENTIONS a type look like a signature that RETURNS it.
    /// </para>
    /// </summary>
    internal static string? EnclosingMethodDeclaration(string code, int index)
    {
        var openers = new Stack<int>();
        for (var i = 0; i < index; i++)
        {
            if (code[i] == '{')
                openers.Push(i);
            else if (code[i] == '}')
            {
                if (openers.Count > 0)
                    openers.Pop();
            }
        }

        while (openers.Count > 0)
        {
            var brace = openers.Pop();
            var declarationLine = DeclarationLineBefore(code, brace);
            if (declarationLine is null || !IsMethodDeclaration(declarationLine))
                continue;

            return SignatureBefore(code, brace);
        }

        return null;
    }

    /// <summary>
    /// The whole signature text between the previous statement/block boundary and
    /// <paramref name="braceIndex"/>, trimmed. Multi-line signatures are returned in full, so a
    /// parameter list spread over several lines is not truncated to its last line.
    /// </summary>
    private static string SignatureBefore(string code, int braceIndex)
    {
        var parens = 0;
        for (var i = braceIndex - 1; i >= 0; i--)
        {
            var c = code[i];
            if (c == ')')
                parens++;
            else if (c == '(')
                parens--;
            else if (parens <= 0 && (c == ';' || c == '{' || c == '}'))
                return code[(i + 1)..braceIndex].Trim();
        }

        return code[..braceIndex].Trim();
    }

    /// <summary>
    /// The nearest preceding non-empty line of <paramref name="code"/> before
    /// <paramref name="braceIndex"/>, or null when there is none.
    /// </summary>
    private static string? DeclarationLineBefore(string code, int braceIndex)
    {
        var lineEnd = code.LastIndexOf('\n', Math.Max(0, braceIndex - 1));
        if (lineEnd < 0)
            return null;

        var cursor = lineEnd;
        while (cursor > 0)
        {
            var previousEnd = code.LastIndexOf('\n', cursor - 1);
            var start = previousEnd < 0 ? 0 : previousEnd + 1;
            var line = code[start..cursor].Trim();
            if (line.Length > 0)
                return line;
            cursor = start;
        }

        return null;
    }

    /// <summary>
    /// A method declaration is a line that carries a parameter list and is not a control-flow
    /// statement, an object creation or an expression-bodied member.
    /// </summary>
    private static bool IsMethodDeclaration(string line)
    {
        if (!line.Contains('(') || !line.Contains(')'))
            return false;

        var trimmed = line.TrimStart();
        foreach (var keyword in new[]
        {
            "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "fixed",
            "return", "new", "yield", "do", "else", "try", "=>",
        })
        {
            if (trimmed.StartsWith(keyword, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts the brace-balanced body of the type whose declaration contains
    /// <paramref name="declarationMarker"/>, starting at that declaration's opening brace. Returns
    /// null when the marker is absent or the body is unbalanced.
    /// </summary>
    internal static string? ExtractTypeBlock(string code, string declarationMarker)
    {
        var markerIndex = code.IndexOf(declarationMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        var open = code.IndexOf('{', markerIndex);
        if (open < 0)
            return null;

        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{')
                depth++;
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return code[markerIndex..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the brace-balanced body of the method whose declaration contains
    /// <paramref name="declarationMarker"/>, starting at that declaration's opening brace. Returns
    /// null when the marker is absent or the body is unbalanced.
    /// </summary>
    internal static string? ExtractMethodBlock(string code, string declarationMarker)
    {
        var markerIndex = code.IndexOf(declarationMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        var open = code.IndexOf('{', markerIndex);
        if (open < 0)
            return null;

        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{')
                depth++;
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return code[markerIndex..(i + 1)];
            }
        }

        return null;
    }
}
