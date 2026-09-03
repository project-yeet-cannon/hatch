using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// Who gets handed an operator app's bundle - admin's, and Hatch's. Three
/// things have to hold, and each is a different kind of bug if it does not: the
/// deep links are covered as well as the assets (or /apps/admin/devices serves
/// index.html to anyone), the refusal is a 404 rather than a 403 (or it
/// advertises what it is withholding), and every other app is untouched (or a
/// family member loses the dashboard to a boundary that was never about them).
/// </summary>
public class AdminAppMiddlewareTests
{
    [Theory]
    // The bundle itself, the deep link a hard refresh produces, and the
    // hashed asset - the first is UseStaticFiles, the second is
    // MapFallbackToFile, and the third would be a hole in either.
    [InlineData("/apps/admin")]
    [InlineData("/apps/admin/")]
    [InlineData("/apps/admin/devices")]
    [InlineData("/apps/admin/assets/index-BGJobmXl.js")]
    // Case, because a path is not case sensitive to the file system this is
    // served from and a guard that is would be trivially stepped around.
    [InlineData("/APPS/Admin/")]
    // Hatch, the second operator app behind the same boundary. Its deep links
    // matter more than most - /apps/hatch/issues/AER-12 is what gets pasted
    // into a chat window - so a gate that covered only the root would be a
    // gate that leaked every ticket in the house.
    [InlineData("/apps/hatch")]
    [InlineData("/apps/hatch/")]
    [InlineData("/apps/hatch/issues/AER-12")]
    [InlineData("/apps/hatch/assets/index-CO5Z7yjM.js")]
    public async Task WithholdsTheBundleFromEveryoneElse(string path)
    {
        var (context, served) = await Run(path, isAdmin: false);

        Assert.False(served);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        // A cached refusal outlives the checkbox that fixes it, and "I made her
        // an admin and her phone still says the page isn't there" is not a
        // symptom anyone connects to a cache entry.
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    /// <summary>
    /// A 404 rather than a 403 on purpose: it is indistinguishable from an
    /// install built without the bundle, which several are - Program.cs mounts
    /// each SPA only if its directory exists. The API's refusals go the other
    /// way, and RequireAdminAttribute says why.
    /// </summary>
    [Fact]
    public async Task TheRefusalNeverAdmitsThereIsSomethingThere()
    {
        var (context, _) = await Run("/apps/admin/", isAdmin: false);

        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Empty(context.Response.Headers.Location.ToString());
        Assert.Equal(0, context.Response.ContentLength ?? 0);
    }

    [Theory]
    [InlineData("/apps/admin/devices")]
    [InlineData("/apps/hatch/issues/AER-12")]
    public async Task ServesItToAnAdministrator(string path)
    {
        var (context, served) = await Run(path, isAdmin: true);

        Assert.True(served);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    // Every app that is not this one, plus the API - guarded separately, by an
    // attribute, on the actions that want it.
    [InlineData("/apps/dashboard/")]
    [InlineData("/apps/family/storage")]
    [InlineData("/apps/auth/")]
    [InlineData("/apps/docs/")]
    [InlineData("/api/zones")]
    [InlineData("/media/Beatles/Revolver/01.flac")]
    // Segment matching, the same rule the wall's allow-list uses: a path that
    // merely starts with the same characters is a different app.
    [InlineData("/apps/administration/")]
    [InlineData("/apps/adminfoo")]
    [InlineData("/apps/hatchery/")]
    [InlineData("/apps/hatchfoo")]
    public async Task LeavesEverythingElseAlone(string path)
    {
        var (_, served) = await Run(path, isAdmin: false);

        Assert.True(served);
    }

    /// <summary>
    /// The rollback, and the whole of local development. Nothing is looked at -
    /// not the path, not the caller - so an install that has not turned
    /// enforcement on pays nothing for this being in the pipeline.
    /// </summary>
    [Fact]
    public async Task NoOpsEntirelyWhileEnforcementIsDormant()
    {
        var gate = new StubAdminGate { Enabled = false };

        var (_, served) = await Run("/apps/admin/", gate: gate);

        Assert.True(served);
        Assert.Equal(0, gate.Evaluations);
    }

    /// <summary>
    /// The gate is asked about the request it is guarding, because the Warning
    /// it logs is what an operator reads when somebody reports the page
    /// missing - and a refusal with the wrong path on it is worse than none.
    /// </summary>
    [Fact]
    public async Task AsksTheGateWithTheRequestItIsAbout()
    {
        var gate = new StubAdminGate { Decision = AdminDecision.Refuse(AdminDecision.NotAdmin) };

        await Run("/apps/admin/settings", gate: gate);

        Assert.Equal(("GET", "/apps/admin/settings", "10.0.0.7"), gate.LastAsked);
    }

    private static async Task<(HttpContext Context, bool Served)> Run(
        string path, bool isAdmin = false, IAdminGate? gate = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("home.example.com");
        context.Request.Path = path;

        gate ??= new StubAdminGate
        {
            Decision = isAdmin
                ? AdminDecision.Allow(new EfPerson
                {
                    Id = Guid.NewGuid(),
                    Name = "Ada",
                    IsAdmin = true,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    UpdatedAt = DateTimeOffset.UnixEpoch,
                })
                : AdminDecision.Refuse(AdminDecision.NotAdmin),
        };

        var served = false;
        var middleware = new AdminAppMiddleware(_ =>
        {
            served = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, gate);

        return (context, served);
    }
}

/// <summary>
/// Answers however the test says, and records what it was asked - so a test can
/// prove the dormant path never asked at all, which is the property that keeps
/// this middleware free on an install that has not turned enforcement on.
/// </summary>
internal sealed class StubAdminGate : IAdminGate
{
    public bool Enabled { get; set; } = true;

    public AdminDecision Decision { get; set; } = AdminDecision.Refuse(AdminDecision.NotAdmin);

    public int Evaluations { get; private set; }

    public (string? Method, string? Path, string? ClientIp) LastAsked { get; private set; }

    public Task<AdminDecision> EvaluateAsync(string? method, PathString path, string? clientIp, CancellationToken ct)
    {
        Evaluations++;
        LastAsked = (method, path.Value, clientIp);
        return Task.FromResult(Decision);
    }
}
