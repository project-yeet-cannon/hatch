using System.Net;

namespace Hatch.Cli.Tests;

/// <summary>
/// <c>api</c> - the raw passthrough, where a non-2xx is the answer and not a
/// fault.
/// </summary>
public sealed class ApiCommandTests
{
    [Fact]
    public async Task A_get_prints_the_body_it_was_answered_with()
    {
        using var h = new CliHarness();
        h.Wire.Reply("GET", "/api/hatch/statuses", HttpStatusCode.OK, """[{"id":1,"name":"To Do"}]""");

        Assert.Equal(0, await new ApiCommand(h.Cli).RunAsync(["GET", "/api/hatch/statuses"], default));
        Assert.Equal("""[{"id":1,"name":"To Do"}]""", h.Said);
    }

    /// <summary>The path is written with a leading slash everywhere, and works without one.</summary>
    [Fact]
    public async Task A_path_with_no_leading_slash_reaches_the_same_route()
    {
        using var h = new CliHarness();
        h.Wire.Reply("GET", "/api/hatch/statuses", HttpStatusCode.OK, "[]");

        Assert.Equal(0, await new ApiCommand(h.Cli).RunAsync(["GET", "api/hatch/statuses"], default));
        Assert.Equal(1, h.Wire.Count("GET", "/api/hatch/statuses"));
    }

    [Fact]
    public async Task The_method_is_taken_however_it_was_typed()
    {
        using var h = new CliHarness();
        h.Wire.Reply("PATCH", "/api/hatch/issues/AER-12", HttpStatusCode.OK, "{}");

        Assert.Equal(0, await new ApiCommand(h.Cli).RunAsync(
            ["patch", "/api/hatch/issues/AER-12", """{"dueAt":"2026-10-01"}"""], default));

        Assert.Equal(1, h.Wire.Count("PATCH", "/api/hatch/issues/AER-12"));
    }

    /// <summary>
    /// The body goes out as it was typed. Re-encoding it would send the text of
    /// the object rather than the object.
    /// </summary>
    [Fact]
    public async Task The_body_is_sent_as_the_json_it_already_was()
    {
        using var h = new CliHarness();
        h.Wire.Reply("POST", "/api/hatch/issues", HttpStatusCode.OK, "{}");

        await new ApiCommand(h.Cli).RunAsync(
            ["POST", "/api/hatch/issues", """{"title":"A thing","type":"task"}"""], default);

        Assert.Equal("""{"title":"A thing","type":"task"}""", h.Wire.To("POST", "/api/hatch/issues").Single().Body);
    }

    [Fact]
    public async Task A_call_with_no_body_sends_none()
    {
        using var h = new CliHarness();
        h.Wire.Reply("DELETE", "/api/hatch/issues/AER-12/dependencies/AER-11", HttpStatusCode.NoContent);

        await new ApiCommand(h.Cli).RunAsync(["DELETE", "/api/hatch/issues/AER-12/dependencies/AER-11"], default);
        Assert.Equal("", h.Wire.To("DELETE", "/api/hatch/issues/AER-12/dependencies/AER-11").Single().Body);
    }

    /// <summary>
    /// Here a refusal is what was asked for, so it comes back as a sentence and
    /// an exit code rather than as a thrown fault.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_printed_and_exits_one_rather_than_throwing()
    {
        using var h = new CliHarness();
        h.Wire.Reply("GET", "/api/hatch/issues/AER-99", HttpStatusCode.NotFound);

        Assert.Equal(1, await new ApiCommand(h.Cli).RunAsync(["GET", "/api/hatch/issues/AER-99"], default));
        Assert.Contains("404 - no such issue or route: /api/hatch/issues/AER-99", h.Complained);
        Assert.Empty(h.Say.Said);
    }

    [Fact]
    public async Task A_204_prints_nothing_and_is_not_a_failure()
    {
        using var h = new CliHarness();
        h.Wire.Reply("DELETE", "/api/hatch/issues/AER-12/claim", HttpStatusCode.NoContent);

        Assert.Equal(0, await new ApiCommand(h.Cli).RunAsync(["DELETE", "/api/hatch/issues/AER-12/claim"], default));
        Assert.Empty(h.Say.Said);
    }

    [Fact]
    public async Task Too_few_arguments_is_the_mistake_and_then_the_usage_block()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new ApiCommand(h.Cli).RunAsync(["GET"], default));
        Assert.Contains("api takes a method, a path and a body", h.Complained);
        Assert.Contains("usage: hatch api", h.Complained);
        Assert.Empty(h.Wire.Calls);
    }

    [Fact]
    public async Task Dash_h_prints_its_own_usage_and_calls_nothing()
    {
        using var h = new CliHarness();

        Assert.Equal(0, await new ApiCommand(h.Cli).RunAsync(["-h"], default));
        Assert.StartsWith("usage: hatch api", h.Said);
        Assert.Empty(h.Wire.Calls);
    }
}
