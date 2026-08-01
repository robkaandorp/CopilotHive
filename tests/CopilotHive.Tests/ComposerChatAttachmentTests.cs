using CopilotHive.Components.Pages;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Pure composition tests for the Composer chat attachment UI helpers in
/// <see cref="ComposerChat"/>. These tests exercise static methods directly through
/// <c>InternalsVisibleTo</c>; no bUnit rendering or controller seam is required.
/// </summary>
public sealed class ComposerChatAttachmentTests
{
    #region BuildAttachmentMessage

    [Fact]
    public void BuildAttachmentMessage_TextWithImage_IncludesImageKindAndPathsOnce()
    {
        var result = ComposerChat.BuildAttachmentMessage(
            "Fix this bug",
            "screenshot.png",
            ".png",
            "/app/state/composer-attachments/abc123.png");

        Assert.Equal(
            "Fix this bug [Attached image file \"screenshot.png\"; saved at \"/app/state/composer-attachments/abc123.png\". "
            + "To analyze it, delegate to a vision-capable sub-agent with start_sub_agent(task: \"Analyze this attachment\", "
            + "image_paths: [\"/app/state/composer-attachments/abc123.png\"]).]",
            result);
    }

    [Fact]
    public void BuildAttachmentMessage_PdfExtension_UsesPdfKind()
    {
        var result = ComposerChat.BuildAttachmentMessage(
            "See attached document",
            "report.pdf",
            ".pdf",
            "/app/state/composer-attachments/xyz789.pdf");

        Assert.StartsWith(
            "See attached document [Attached pdf file \"report.pdf\"; saved at \"/app/state/composer-attachments/xyz789.pdf\"",
            result);
    }

    [Fact]
    public void BuildAttachmentMessage_WhitespaceText_UsesAttachmentOnlyForm()
    {
        var result = ComposerChat.BuildAttachmentMessage(
            "   ",
            "doc.pdf",
            ".pdf",
            "/path/to/file.pdf");

        Assert.Equal(
            "The user attached a file with no accompanying text. [Attached pdf file \"doc.pdf\"; saved at \"/path/to/file.pdf\". "
            + "To analyze it, delegate to a vision-capable sub-agent with start_sub_agent(task: \"Analyze this attachment\", "
            + "image_paths: [\"/path/to/file.pdf\"]).]",
            result);
    }

    [Fact]
    public void BuildAttachmentMessage_NullText_UsesAttachmentOnlyForm()
    {
        var result = ComposerChat.BuildAttachmentMessage(
            null,
            "image.png",
            ".png",
            "/path/to/image.png");

        Assert.Equal(
            "The user attached a file with no accompanying text. [Attached image file \"image.png\"; saved at \"/path/to/image.png\". "
            + "To analyze it, delegate to a vision-capable sub-agent with start_sub_agent(task: \"Analyze this attachment\", "
            + "image_paths: [\"/path/to/image.png\"]).]",
            result);
    }

    [Fact]
    public void BuildAttachmentMessage_PathWithBackslashAndQuote_IsJsonEscapedInBothPositions()
    {
        var result = ComposerChat.BuildAttachmentMessage(
            "text",
            "file.png",
            ".png",
            @"C:\Users\test\" + ("\"" + "file.png"));

        Assert.Equal(
            "text [Attached image file \"file.png\"; saved at \"C:\\\\Users\\\\test\\\\\\u0022file.png\". "
            + "To analyze it, delegate to a vision-capable sub-agent with start_sub_agent(task: \"Analyze this attachment\", "
            + "image_paths: [\"C:\\\\Users\\\\test\\\\\\u0022file.png\"]).]",
            result);
    }

    #endregion

    #region ComputeCanSend

    [Fact]
    public void ComputeCanSend_TextOnly_ReturnsTrue()
    {
        Assert.True(ComposerChat.ComputeCanSend(false, false, "hello", null));
    }

    [Fact]
    public void ComputeCanSend_AttachmentOnly_ReturnsTrue()
    {
        Assert.True(ComposerChat.ComputeCanSend(false, false, "", new object()));
    }

    [Fact]
    public void ComputeCanSend_EmptyInputNoAttachment_ReturnsFalse()
    {
        Assert.False(ComposerChat.ComputeCanSend(false, false, "", null));
    }

    [Fact]
    public void ComputeCanSend_WhitespaceOnlyNoAttachment_ReturnsFalse()
    {
        Assert.False(ComposerChat.ComputeCanSend(false, false, "   ", null));
    }

    [Fact]
    public void ComputeCanSend_Streaming_ReturnsFalse()
    {
        Assert.False(ComposerChat.ComputeCanSend(true, false, "hello", new object()));
    }

    [Fact]
    public void ComputeCanSend_Uploading_ReturnsFalse()
    {
        Assert.False(ComposerChat.ComputeCanSend(false, true, "hello", new object()));
    }

    #endregion
}
