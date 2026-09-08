namespace Aerie.Hatch;

/// <summary>
/// The raw passthrough: a method, a path, and a body that is already JSON.
/// </summary>
/// <remarks>
/// Fifteen commands are the calls a working session actually makes. This one is
/// everything else - filing an issue, setting a date, reading the plan - because
/// the alternative to one honest escape hatch is fifty thin wrappers, each of
/// which is a second place the API is written down.
///
/// It goes through <see cref="HatchClient.Send"/> rather than the throwing
/// reads, since here a non-2xx is the answer and not a fault: somebody asking
/// what a route says wants to see what it said.
/// </remarks>
public sealed class ApiCommand(Cli cli)
{
    public static readonly string[] ApiUsage =
    [
        "usage: hatch api <METHOD> <path> [<json body>]",
        "",
        "  hatch api GET /api/hatch/issues?statusId=2",
        "  hatch api PATCH /api/hatch/issues/AER-12 '{\"dueAt\":\"2026-10-01\"}'",
        "",
        "  The body is sent as it was typed. docs/hatch-planning.md has the shapes",
        "  for filing and reshaping work.",
    ];

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, ApiUsage);
        if (args.Length is < 2 or > 3) return Usage.Refuse(cli.Say, "api takes a method, a path and a body", ApiUsage);

        var method = new HttpMethod(args[0].ToUpperInvariant());
        var path = "/" + args[1].TrimStart('/');
        object? body = args.Length == 3 && args[2].Length > 0 ? new RawJson(args[2]) : null;

        var answer = await cli.Board.Client.Send(method, path, body, ct);

        if (!answer.Ok)
        {
            cli.Say.Complain(cli.Board.Client.Refusal(answer, path));
            return 1;
        }

        if (answer.Body.Length > 0) cli.Say.Lines(answer.Body.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'));
        return 0;
    }
}
