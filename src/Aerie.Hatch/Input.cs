namespace Aerie.Hatch;

/// <summary>
/// Where the two interactive commands read a typed reply.
/// </summary>
/// <remarks>
/// An object for the same reason <see cref="Terminal"/> is one: <c>config</c>
/// and <c>answer</c> are conversations, and a conversation is only testable if
/// the replies can be handed to it. It also puts the one masked read in the
/// program in a single place - .NET has no <c>read -rs</c>, so the key prompt
/// is a hand-rolled loop over <see cref="Console.ReadKey(bool)"/>.
/// </remarks>
public class Input
{
    /// <summary>
    /// Whether there is somebody there to answer. The equivalent of
    /// <c>[ -t 0 ]</c>: a redirected stdin is a pipe, and asking it a question
    /// is a command that hangs.
    /// </summary>
    public virtual bool Interactive => !Console.IsInputRedirected;

    /// <summary>The question, with no newline after it, so the answer lands on the same line.</summary>
    public virtual void Prompt(string text) => Console.Write(text);

    /// <summary>A line, or null at end of input - which is the gesture for "I am done here".</summary>
    public virtual string? Line() => Console.ReadLine();

    /// <summary>
    /// A line that never reaches the screen: the one value on it that a
    /// screenshot, a shoulder or a scrollback should not be able to keep.
    /// </summary>
    /// <remarks>
    /// Falls back to an echoing read where there is no console to intercept
    /// keys on - a piped stdin has no keys, and refusing there would make the
    /// command untestable and unscriptable for the sake of hiding nothing.
    /// </remarks>
    public virtual string? Secret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine();

        var typed = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return typed.ToString();

                case ConsoleKey.Backspace when typed.Length > 0:
                    typed.Length--;
                    break;

                case ConsoleKey.Escape:
                    typed.Clear();
                    break;

                default:
                    if (!char.IsControl(key.KeyChar)) typed.Append(key.KeyChar);
                    break;
            }
        }
    }
}

/// <summary>Replies from a list, and nothing read from a terminal. What a test hands in.</summary>
public sealed class Replies(params string?[] lines) : Input
{
    private readonly Queue<string?> _lines = new(lines);

    /// <summary>Every prompt that was put to it, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>
    /// Whether this is standing in for a person. Cleared by the tests that
    /// assert the two "needs a terminal" refusals.
    /// </summary>
    public bool AtATerminal { get; set; } = true;

    public override bool Interactive => AtATerminal;

    public override void Prompt(string text) => Asked.Add(text);

    public override string? Line() => _lines.Count > 0 ? _lines.Dequeue() : null;

    public override string? Secret() => Line();
}
