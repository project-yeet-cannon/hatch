using System.Text;
using System.Text.RegularExpressions;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// Reads one <c>docs/plans/*.md</c> file and says what issues it would become:
/// the document is an epic, each <c>## Phase</c> section is a story, each
/// checkbox under a phase is a task.
///
/// It is a pure function - no database, no clock, no actor - so the preview
/// endpoint and the import endpoint agree by construction rather than by two
/// implementations that happen to match. What the operator sees on the preview
/// page is literally the tree that gets written.
/// </summary>
/// <remarks>
/// Deliberately a shape reader rather than a markdown parser. It knows four
/// things - an H1, an H2, a checkbox, and an indented continuation line - and
/// treats everything else as text to carry across unchanged. A plan is prose
/// written for a person, and a parser that tried to understand the prose would
/// be a parser that broke the first time somebody wrote a table.
/// </remarks>
public partial class PlanImportParser
{
    /// <summary>
    /// The heading that opens a story. Case-insensitive because the rule is
    /// about the shape of a plan document, not about anybody's shift key.
    /// </summary>
    private const string PhasePrefix = "Phase";

    public ParsedEpic Parse(string filename, string content)
    {
        var source = SourceName(filename);
        var lines = Lines(content);

        string? title = null;
        var epicBody = new StringBuilder();
        var stories = new List<StoryDraft>();
        StoryDraft? story = null;

        foreach (var line in lines)
        {
            // The H1 is the epic's title, not part of its description - a
            // description that opens by repeating the title reads like a bug.
            // Only the first one: a second H1 is somebody's document, not a
            // second epic.
            if (title is null && H1().Match(line) is { Success: true } h1 && h1.Groups[1].Value.Trim() is { Length: > 0 } heading1)
            {
                title = heading1;
                continue;
            }

            if (H2().Match(line) is { Success: true } h2)
            {
                var heading = h2.Groups[1].Value.Trim();

                if (heading.StartsWith(PhasePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    story = new StoryDraft(heading);
                    stories.Add(story);
                }
                else
                {
                    // "Decisions", "Non-goals", "How to work this plan": context
                    // for the whole plan rather than a unit of work, so it stays
                    // in the epic - heading and all, wherever in the file it sat.
                    story = null;
                    epicBody.AppendLine(line);
                }

                continue;
            }

            if (story is null)
            {
                epicBody.AppendLine(line);
                continue;
            }

            story.Add(line);
        }

        var parsed = stories.Select(s => s.ToStory(source)).ToList();

        return new ParsedEpic(
            source,
            Title(title, source),
            Describe(epicBody.ToString(), source),
            RollUp(parsed.Select(s => s.State)),
            parsed);
    }

    /// <summary>
    /// A story being filled: its heading, the prose under it, and its
    /// checkboxes. Mutable and private because it exists only for the length of
    /// one <see cref="Parse"/> call - the DTOs it turns into are what leaves.
    /// </summary>
    private sealed class StoryDraft(string title)
    {
        private readonly StringBuilder _body = new();
        private readonly List<TaskDraft> _tasks = [];

        /// <summary>
        /// The checkbox still collecting its continuation lines, if any. A plan
        /// wraps a long task across several indented lines, and those lines
        /// belong to the task rather than to the story's description - which is
        /// the whole reason this parser tracks anything between lines at all.
        /// </summary>
        private TaskDraft? _open;

        public void Add(string line)
        {
            if (Checkbox().Match(line) is { Success: true } box)
            {
                // Nested checkboxes flatten: a sub-task under a task is still a
                // thing to do, and a two-level board would be a tree view
                // nobody asked for.
                _open = new TaskDraft(box.Groups[1].Value is not " ", box.Groups[2].Value.Trim());
                _tasks.Add(_open);
                return;
            }

            // Indented and not blank, directly under a checkbox: a wrapped line
            // of that checkbox. A blank line or a line at the margin ends it.
            if (_open is not null && line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Length > 0)
            {
                _open.Continue(line.Trim());
                return;
            }

            _open = null;
            _body.AppendLine(line);
        }

        public ParsedStory ToStory(string source)
        {
            // An empty box - "- [ ]" with nothing after it - is a formatting
            // artifact rather than a thing to do, and an issue with no title is
            // not something the API would accept anyway.
            var tasks = _tasks.Select(t => t.ToTask(source)).Where(t => t.Title.Length > 0).ToList();
            return new ParsedStory(Clip(title), Describe(_body.ToString(), source), RollUp(tasks.Select(t => t.State)), tasks);
        }
    }

    /// <summary>One checkbox, and however many lines it wrapped onto.</summary>
    private sealed class TaskDraft(bool done, string text)
    {
        private readonly StringBuilder _text = new(text);

        public void Continue(string line) => _text.Append(' ').Append(line);

        /// <summary>
        /// The whole item is the description and as much of it as fits is the
        /// title. Plans are written with sentence-long tasks, so the card gets
        /// the sentence it can hold and the detail page gets all of it - rather
        /// than the first physical line, which is a wrap point the author chose
        /// for a text editor's width and not a summary of anything.
        /// </summary>
        public ParsedTask ToTask(string source)
        {
            var text = _text.ToString().Trim();
            return new ParsedTask(Clip(text), Describe(text, source), done ? PlanState.Done : PlanState.Todo);
        }
    }

    // ---- Shared rules ----

    /// <summary>
    /// Where a parent sits given what is under it: everything done is done,
    /// anything started is started, and nothing at all is not started - an
    /// empty phase is a phase nobody has begun, not a phase that shipped.
    /// </summary>
    private static PlanState RollUp(IEnumerable<PlanState> children)
    {
        var states = children.ToList();

        if (states.Count == 0) return PlanState.Todo;
        if (states.All(s => s == PlanState.Done)) return PlanState.Done;
        return states.Any(s => s != PlanState.Todo) ? PlanState.InProgress : PlanState.Todo;
    }

    /// <summary>
    /// A description, with the line that says where it came from. Every
    /// imported issue carries it: an issue whose text was written somewhere
    /// else should say so on its face, not only in its event trail.
    /// </summary>
    private static string Describe(string body, string source)
    {
        var trimmed = body.Trim();
        var footer = $"_Imported from `{source}`_";

        // The cap is the column's, and it is reached only by a pathological
        // file; the footer survives the cut because the provenance is the part
        // that cannot be reconstructed from the source document.
        var room = EfHatchIssue.MaxDescriptionLength - footer.Length - 2;
        if (trimmed.Length > room) trimmed = trimmed[..room];

        return trimmed.Length == 0 ? footer : $"{trimmed}\n\n{footer}";
    }

    /// <summary>
    /// The title, cut at a word boundary when the text is longer than a title
    /// column can hold. The ellipsis is there so a truncated card reads as
    /// truncated rather than as a sentence somebody forgot to finish.
    /// </summary>
    private static string Clip(string text)
    {
        if (text.Length <= EfHatchIssue.MaxTitleLength) return text;

        var cut = text[..(EfHatchIssue.MaxTitleLength - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > EfHatchIssue.MaxTitleLength / 2 ? cut[..space] : cut.TrimEnd()) + "…";
    }

    /// <summary>The epic's title: its H1, or failing that the name it came in as.</summary>
    private static string Title(string? h1, string source) =>
        h1 is { Length: > 0 } ? Clip(h1) : Clip(WithoutMarkdownSuffix(source));

    /// <summary>
    /// The source name with its markdown extension taken off, and nothing else
    /// touched.
    ///
    /// Specifically not <c>Path.GetFileNameWithoutExtension</c>, which drops
    /// everything after the *last* dot whatever it is. That is the same answer
    /// for every name this started with - the upload path refuses anything not
    /// ending <c>.md</c> - but a pasted plan's name is a title somebody typed,
    /// and "Phase 5.1 importer" is not a file with a ".1 importer" extension.
    /// It would have been filed as "Phase 5".
    /// </summary>
    private static string WithoutMarkdownSuffix(string source)
    {
        foreach (var suffix in (string[])[".md", ".markdown"])
        {
            if (source.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return source[..^suffix.Length];
        }

        return source;
    }

    /// <summary>
    /// The name this document is remembered by. Reduced to its last segment so
    /// a browser that sends a path - or somebody who types one - cannot write a
    /// directory into a description or an event payload.
    /// </summary>
    private static string SourceName(string filename)
    {
        var name = Path.GetFileName(filename.Replace('\\', '/')).Trim();
        return name.Length > 0 ? name : "upload.md";
    }

    private static string[] Lines(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    // ---- The four shapes it knows ----

    /// <summary>One hash then space: the document's title. <c>##</c> does not match - the second character is not whitespace.</summary>
    [GeneratedRegex(@"^#[ \t]+(.+)$")]
    private static partial Regex H1();

    /// <summary>Exactly two hashes: a section. <c>###</c> does not match, and is carried along as text.</summary>
    [GeneratedRegex(@"^##[ \t]+(.+)$")]
    private static partial Regex H2();

    /// <summary>A list item with a box, at any indentation - group 1 is what is in the box, group 2 the text after it.</summary>
    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+\[([ xX])\][ \t]*(.*)$")]
    private static partial Regex Checkbox();
}
