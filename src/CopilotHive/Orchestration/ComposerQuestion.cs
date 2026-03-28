namespace CopilotHive.Orchestration;

/// <summary>The type of question the Composer is asking the user.</summary>
public enum QuestionType
{
    /// <summary>A yes/no confirmation question.</summary>
    YesNo,

    /// <summary>A single-choice question with a list of options.</summary>
    SingleChoice,

    /// <summary>A multi-choice question where multiple options may be selected.</summary>
    MultiChoice,
}

/// <summary>
/// Represents a pending question from the Composer LLM waiting for a user answer.
/// </summary>
public sealed class ComposerQuestion
{
    /// <summary>The question text to display to the user.</summary>
    public required string Text { get; init; }

    /// <summary>The type of response expected.</summary>
    public QuestionType Type { get; init; }

    /// <summary>The list of selectable options (used for SingleChoice and MultiChoice).</summary>
    public List<string> Options { get; init; } = [];

    /// <summary>Completion source awaited by the streaming loop until the user answers.</summary>
    internal TaskCompletionSource<string> Completion { get; } = new();
}
