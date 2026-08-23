using Aerie.Api.Modules.Game;

namespace Aerie.Api.Tests.Game;

/// <summary>
/// The shapes a real answer arrives in. Every case here is one that has to
/// survive rather than lose a turn someone is waiting on.
/// </summary>
public class GameAnswerParserTests
{
    [Fact]
    public void ReadsTheThreeBlocks()
    {
        var parsed = GameAnswerParser.Parse(
            """
            <summary>The ball can jump now!</summary>
            <extra>I added a longest-jump record.</extra>
            <code>
            defineGame({ setup(w) { w.ball({ x: 0, y: 0 }); } });
            </code>
            """);

        Assert.NotNull(parsed);
        Assert.Equal("The ball can jump now!", parsed.Summary);
        Assert.Equal("I added a longest-jump record.", parsed.Extra);
        Assert.Equal("defineGame({ setup(w) { w.ball({ x: 0, y: 0 }); } });", parsed.Code);
    }

    [Fact]
    public void PeelsAMarkdownFenceFromInsideTheCodeBlock()
    {
        var parsed = GameAnswerParser.Parse(
            """
            <summary>Hills.</summary>
            <code>
            ```js
            defineGame({ setup(w) { w.hill(0, 100, 900, 500); } });
            ```
            </code>
            """);

        // A stray ```js line is a syntax error in a file that is otherwise fine.
        Assert.Equal("defineGame({ setup(w) { w.hill(0, 100, 900, 500); } });", parsed!.Code);
    }

    [Fact]
    public void KeepsCodeWhoseClosingTagNeverArrived()
    {
        var parsed = GameAnswerParser.Parse(
            """
            <summary>A big one.</summary>
            <code>
            defineGame({ setup(w) {
            """);

        // Whether a truncated answer is usable is the caller's decision, made
        // on the stop reason - the parser's job is only to hand back what came.
        Assert.Equal("defineGame({ setup(w) {", parsed!.Code);
    }

    [Fact]
    public void IgnoresProseAroundTheBlocks()
    {
        var parsed = GameAnswerParser.Parse(
            """
            Sure! Here you go.

            <summary>Sky.</summary>
            <code>defineGame({ setup(w) { w.sky('#fff'); } });</code>

            Let me know if you want anything else.
            """);

        Assert.Equal("defineGame({ setup(w) { w.sky('#fff'); } });", parsed!.Code);
        Assert.Equal("Sky.", parsed.Summary);
    }

    [Fact]
    public void AnEmptyExtraIsNoExtra()
    {
        var parsed = GameAnswerParser.Parse("<summary>Sky.</summary><extra></extra><code>defineGame({});</code>");

        Assert.Null(parsed!.Extra);
    }

    [Fact]
    public void SummaryAlwaysHasSomethingToSay()
    {
        var parsed = GameAnswerParser.Parse("<code>defineGame({});</code>");

        // The summary is what the child is told happened. A blank one is worse
        // than a generic one.
        Assert.False(string.IsNullOrWhiteSpace(parsed!.Summary));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I'd rather not build that.")]
    [InlineData("<summary>All talk, no game.</summary>")]
    [InlineData("<code></code>")]
    public void NoCode_IsNoAnswer(string answer)
    {
        Assert.Null(GameAnswerParser.Parse(answer));
    }
}
