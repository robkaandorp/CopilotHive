using Microsoft.Extensions.AI;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    /// <summary>
    /// Returns the recent user/assistant message pairs from the persistent session.
    /// Tool-call messages are formatted as markdown inline code, tool-result messages
    /// as blockquotes, so they appear in the chat history at the correct position.
    /// </summary>
    /// <param name="maxMessages">Maximum number of messages to return.</param>
    public IReadOnlyList<(string Role, string Content)> GetChatHistory(int maxMessages = 50)
    {
        var result = new List<(string Role, string Content)>();
        foreach (var msg in _session.MessageHistory)
        {
            if (msg.Role == Microsoft.Extensions.AI.ChatRole.User)
            {
                var text = msg.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(("user", text));
            }
            else if (msg.Role == Microsoft.Extensions.AI.ChatRole.Assistant)
            {
                // Check if this assistant message contains tool calls
                var functionCalls = msg.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>().ToList();
                if (functionCalls.Count > 0)
                {
                    // Include any text preceding the tool calls
                    var text = msg.Text;
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(("assistant", text));

                    // Format each tool call as markdown
                    var sb = new System.Text.StringBuilder();
                    foreach (var fc in functionCalls)
                    {
                        var argsStr = FormatToolCallArgs(fc.Arguments);
                        sb.AppendLine($"`🔧 {fc.Name}({argsStr})`");
                    }
                    result.Add(("assistant", sb.ToString().TrimEnd()));
                }
                else
                {
                    var text = msg.Text;
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(("assistant", text));
                }
            }
            else if (msg.Role == Microsoft.Extensions.AI.ChatRole.Tool)
            {
                // Format tool results as blockquote
                var results = msg.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().ToList();
                if (results.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var fr in results)
                    {
                        var resultStr = fr.Result?.ToString() ?? "(no result)";
                        var firstLine = TruncateFirstLine(resultStr, 120);
                        sb.AppendLine($"> {firstLine}");
                    }
                    result.Add(("assistant", sb.ToString().TrimEnd()));
                }
            }
        }

        if (result.Count > maxMessages)
            return result.GetRange(result.Count - maxMessages, maxMessages);

        return result;
    }

    private static string FormatToolCallArgs(IDictionary<string, object?>? args, int maxLength = 100)
    {
        if (args == null || args.Count == 0) return "";
        var parts = args.Select(a =>
        {
            var val = a.Value?.ToString() ?? "null";
            if (val.Length > 40) val = val[..39] + "…";
            return $"{a.Key}=\"{val}\"";
        });
        var joined = string.Join(", ", parts);
        if (joined.Length > maxLength) joined = joined[..(maxLength - 1)] + "…";
        return joined;
    }

    private static string TruncateFirstLine(string text, int maxLength)
    {
        var newline = text.IndexOfAny(['\n', '\r']);
        var line = newline >= 0 ? text[..newline] : text;
        if (line.Length > maxLength) line = line[..(maxLength - 1)] + "…";
        return line;
    }
}
