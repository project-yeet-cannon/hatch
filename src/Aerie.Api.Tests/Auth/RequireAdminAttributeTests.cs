using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.Auth;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// The API half of the boundary. It runs as an authorization filter rather than
/// inside the action, which is the property worth pinning: a refused request
/// must not reach model binding or the action body, or a guarded endpoint has
/// already done half of what it was told not to.
/// </summary>
public class RequireAdminAttributeTests
{
    [Fact]
    public async Task LetsAnAdministratorThrough()
    {
        var context = NewContext(new StubAdminGate
        {
            Decision = AdminDecision.Allow(new EfPerson
            {
                Id = Guid.NewGuid(),
                Name = "Ada",
                IsAdmin = true,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            }),
        });

        await new RequireAdminAttribute().OnAuthorizationAsync(context);

        // A null Result is the filter pipeline's "carry on" - anything else
        // short-circuits before the action runs.
        Assert.Null(context.Result);
    }

    /// <summary>
    /// The rollback. An install that has not turned enforcement on behaves
    /// exactly as it did before these attributes existed, which is the only
    /// reason it was safe to put them on thirty-odd actions at once.
    /// </summary>
    [Fact]
    public async Task LetsEveryoneThroughWhileEnforcementIsDormant()
    {
        var context = NewContext(new StubAdminGate { Enabled = false, Decision = AdminDecision.Dormant });

        await new RequireAdminAttribute().OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    /// <summary>
    /// A 403, not the 404 the admin app's bundle gets. The entities behind
    /// these verbs are already listed by unguarded GETs, so there is nothing
    /// left for a 404 to conceal and a great deal for it to confuse.
    /// </summary>
    [Theory]
    [InlineData(AdminDecision.NotAdmin)]
    [InlineData(AdminDecision.NoPerson)]
    [InlineData(AdminDecision.NoGrant)]
    public async Task RefusesEveryoneElseWithAReasonTheAdminAppCanRender(string reason)
    {
        var context = NewContext(new StubAdminGate { Decision = AdminDecision.Refuse(reason) });

        await new RequireAdminAttribute().OnAuthorizationAsync(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        // The two refusals want different sentences on screen: an unlinked
        // device is fixed on the Sessions page, a non-admin is fixed by
        // somebody else.
        Assert.Equal(reason, Assert.IsType<AuthErrorDto>(result.Value).Error);
    }

    [Fact]
    public async Task AsksAboutTheRequestItIsGuarding()
    {
        var gate = new StubAdminGate();
        var context = NewContext(gate);
        context.HttpContext.Request.Method = HttpMethods.Delete;
        context.HttpContext.Request.Path = "/api/zones/2b1a";

        await new RequireAdminAttribute().OnAuthorizationAsync(context);

        Assert.Equal(("DELETE", "/api/zones/2b1a", "10.0.0.7", null), gate.LastAsked);
    }

    private static AuthorizationFilterContext NewContext(IAdminGate gate)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gate);

        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = "/api/people";

        return new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            []);
    }
}
