using System.Net;

namespace Hatch.Cli.Tests;

/// <summary>
/// The one branch every call takes before it is made: a key present is a
/// credential, a key absent is a name - and the 401 that comes back says which
/// of the two happened.
/// </summary>
public sealed class HatchClientTests
{
    private static Settings Settings(string key) =>
        new() { Base = "https://hatch.example", Key = key, HeartbeatSeconds = 0 };

    /// <summary>
    /// A key is a credential and outranks a name; the header is only a name, and
    /// only a Hatch with its wall off reads one. One or the other, never both.
    /// </summary>
    [Fact]
    public async Task A_key_is_sent_as_a_bearer_and_no_runner_header_goes_with_it()
    {
        using var wire = new HeaderWire();
        using var client = new HatchClient(Settings("hatch_ak_test"), "test:/checkout", wire);

        await client.Send(HttpMethod.Get, "/api/hatch/board", null, default);

        Assert.True(client.Keyed);
        Assert.Equal("Bearer hatch_ak_test", wire.Authorization);
        Assert.Null(wire.Runner);
    }

    [Fact]
    public async Task No_key_names_the_runner_instead_and_sends_no_authorization()
    {
        using var wire = new HeaderWire();
        using var client = new HatchClient(Settings(""), "test:/checkout", wire);

        await client.Send(HttpMethod.Get, "/api/hatch/board", null, default);

        Assert.False(client.Keyed);
        Assert.Null(wire.Authorization);
        Assert.Equal("test:/checkout", wire.Runner);
    }

    /// <summary>The same header the server reads, spelled once on each side.</summary>
    [Fact]
    public void The_runner_header_is_the_one_the_server_reads()
    {
        Assert.Equal("X-Hatch-Runner", HatchClient.RunnerHeader);
    }

    [Fact]
    public async Task A_key_of_only_spaces_is_no_key()
    {
        using var wire = new HeaderWire();
        using var client = new HatchClient(Settings("   "), "test:/checkout", wire);

        await client.Send(HttpMethod.Get, "/api/hatch/board", null, default);

        Assert.False(client.Keyed);
        Assert.Equal("test:/checkout", wire.Runner);
    }

    // ---- the two 401s ----

    /// <summary>
    /// A key that was refused is a key to go and look at: minted, not revoked,
    /// copied whole?
    /// </summary>
    [Fact]
    public async Task A_401_against_a_key_that_was_sent_says_the_key_was_not_accepted()
    {
        using var wire = new Wire();
        wire.Reply("GET", "/api/hatch/board", HttpStatusCode.Unauthorized);
        using var client = new HatchClient(Settings("hatch_ak_wrong"), "test:/checkout", wire);

        var thrown = await Assert.ThrowsAsync<HatchException>(
            () => client.GetAsync<BoardDto>("/api/hatch/board", default));

        Assert.Contains("401 - the key was not accepted", thrown.Message);
    }

    /// <summary>
    /// No credential was sent, so nothing was rejected: this Hatch has its wall
    /// up and wants one. Said plainly, because "the key was not accepted" would
    /// send somebody looking at a key they never set.
    /// </summary>
    [Fact]
    public async Task A_401_against_no_key_at_all_says_the_wall_is_on_and_names_config()
    {
        using var wire = new Wire();
        wire.Reply("GET", "/api/hatch/board", HttpStatusCode.Unauthorized);
        using var client = new HatchClient(Settings(""), "test:/checkout", wire);

        var thrown = await Assert.ThrowsAsync<HatchException>(
            () => client.GetAsync<BoardDto>("/api/hatch/board", default));

        Assert.Contains("no key was sent and this Hatch has its wall on", thrown.Message);
        Assert.Contains("hatch config", thrown.Message);
        Assert.DoesNotContain("./scripts/hatch.sh", thrown.Message);
        Assert.DoesNotContain("the key was not accepted", thrown.Message);
    }

    /// <summary>
    /// A 403 is the other way round: the key worked, and the route is not one it
    /// may take.
    /// </summary>
    [Fact]
    public async Task A_403_is_the_key_working_and_the_route_being_refused()
    {
        using var wire = new Wire();
        wire.Reply("GET", "/api/hatch/playbooks", HttpStatusCode.Forbidden);
        using var client = new HatchClient(Settings("hatch_ak_test"), "test:/checkout", wire);

        var thrown = await Assert.ThrowsAsync<HatchException>(
            () => client.GetAsync<BoardDto>("/api/hatch/playbooks", default));

        Assert.Contains("403 - the key is good and this route is not one it may take", thrown.Message);
    }

    /// <summary>
    /// A connection that never happened is a different thing from a refusal, and
    /// every caller treats it as one - the difference matters most to the
    /// heartbeat, where one is a lost lease and the other is a minute of bad
    /// network.
    /// </summary>
    [Fact]
    public async Task An_origin_that_did_not_answer_is_a_null_status_and_not_a_code()
    {
        using var client = new HatchClient(Settings("hatch_ak_test"), "test:/checkout", new DeadWire());

        var answer = await client.Send(HttpMethod.Get, "/api/hatch/board", null, default);

        Assert.Null(answer.Status);
        Assert.False(answer.Ok);
    }

    /// <summary>
    /// The trimmed binary has reflection-based serialization switched off, so
    /// every wire record has to be in the generated table. One that is not says
    /// so by name rather than coming back empty.
    /// </summary>
    [Fact]
    public async Task A_record_nobody_registered_refuses_by_naming_itself()
    {
        using var wire = new Wire();
        wire.Reply("GET", "/api/hatch/board", HttpStatusCode.OK, "{}");
        using var client = new HatchClient(Settings("hatch_ak_test"), "test:/checkout", wire);

        var thrown = await Assert.ThrowsAsync<HatchException>(
            () => client.GetAsync<UnregisteredDto>("/api/hatch/board", default));

        Assert.Contains("UnregisteredDto is not registered in HatchJson", thrown.Message);
    }

    private sealed record UnregisteredDto(string Nothing);

    /// <summary>A wire that keeps the headers it was called with, and answers nothing much.</summary>
    private sealed class HeaderWire : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        public string? Runner { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Runner = request.Headers.TryGetValues(HatchClient.RunnerHeader, out var values)
                ? string.Join(",", values)
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    /// <summary>An origin that is not there.</summary>
    private sealed class DeadWire : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
