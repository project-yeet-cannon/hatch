using Hatch.Api.Ef;
using Hatch.Api.Services.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hatch.Api.Tests.Auth;

/// <summary>
/// The one place Person.IsAdmin is read. Two properties carry everything built
/// on top of it: the gate is inert unless an operator asked for it in config,
/// and when it is not inert it answers from the person on the caller's grant
/// and from nothing else.
///
/// The dormant case is the one worth breaking a build over. It is the whole of
/// local development, the whole of AUTH_MODE=none, and the rollback for an
/// install where enforcement went wrong - a gate that started deciding on its
/// own would take the admin app away from every developer running `make run`,
/// and would take the way *back* away from an operator trying to undo it.
/// </summary>
public class AdminGateTests
{
    [Fact]
    public async Task IsDormantUntilAnOperatorTurnsItOn()
    {
        // The default posture. Nothing is looked at, not even the cookie.
        var caller = new StubCallerIdentity(Grant(Person(isAdmin: false)));
        var gate = NewGate(caller, enabled: true, enforceAdmin: false);

        Assert.False(gate.Enabled);
        var decision = await gate.EvaluateAsync("GET", "/apps/admin/", null, acceptScope: null, default);

        Assert.Equal(AdminOutcome.Dormant, decision.Outcome);
        Assert.True(decision.IsAllowed);
        Assert.Equal(0, caller.Asked);
    }

    /// <summary>
    /// An install with no wall has no identity to read. Enforcing a role there
    /// would refuse everyone, on the one configuration where nobody is enrolled
    /// and nobody can be.
    /// </summary>
    [Fact]
    public async Task IsDormantWhenTheWallItselfIsOff_EvenIfEnforcementWasAskedFor()
    {
        var gate = NewGate(new StubCallerIdentity(null), enabled: false, enforceAdmin: true);

        Assert.False(gate.Enabled);
        Assert.Equal(AdminOutcome.Dormant, (await gate.EvaluateAsync("GET", "/apps/admin/", null, acceptScope: null, default)).Outcome);
    }

    [Fact]
    public async Task AdmitsADeviceBelongingToAnAdministrator()
    {
        var person = Person(isAdmin: true);
        var gate = NewGate(new StubCallerIdentity(Grant(person)));

        var decision = await gate.EvaluateAsync("DELETE", "/api/zones/x", "10.0.0.7", acceptScope: null, default);

        Assert.Equal(AdminOutcome.Admin, decision.Outcome);
        Assert.True(decision.IsAllowed);
        Assert.Same(person, decision.Person);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public async Task RefusesADeviceBelongingToSomeoneWhoIsNotOne()
    {
        var gate = NewGate(new StubCallerIdentity(Grant(Person(isAdmin: false))));

        var decision = await gate.EvaluateAsync("DELETE", "/api/zones/x", "10.0.0.7", acceptScope: null, default);

        Assert.Equal(AdminOutcome.Refused, decision.Outcome);
        Assert.False(decision.IsAllowed);
        Assert.Equal(AdminDecision.NotAdmin, decision.Reason);
        Assert.Null(decision.Person);
    }

    /// <summary>
    /// The hallway tablet, and every grant minted before people existed. It is
    /// a distinct reason from "not an admin" because the fix is different: this
    /// one is a dropdown on the Sessions page, not a checkbox on a person.
    /// </summary>
    [Fact]
    public async Task RefusesAnEnrolledDeviceNobodyHasClaimed()
    {
        var gate = NewGate(new StubCallerIdentity(Grant(person: null)));

        var decision = await gate.EvaluateAsync("POST", "/api/people", "10.0.0.7", acceptScope: null, default);

        Assert.Equal(AdminDecision.NoPerson, decision.Reason);
    }

    /// <summary>
    /// Reachable only where the wall does not enforce in process - the canary,
    /// or a pod reached directly inside the cluster. This gate does not depend
    /// on Auth:EnforceInProcess on purpose, so the admin boundary still stands
    /// on exactly the configuration where the wall's own does not.
    /// </summary>
    [Fact]
    public async Task RefusesACallerWithNoCredentialAtAll()
    {
        var gate = NewGate(new StubCallerIdentity(null));

        var decision = await gate.EvaluateAsync("GET", "/apps/admin/", "10.0.0.7", acceptScope: null, default);

        Assert.Equal(AdminDecision.NoGrant, decision.Reason);
    }

    /// <summary>
    /// The gate asks for the grant once, and reads the person off the same
    /// answer. Asking twice would be harmless here and expensive in
    /// production, where a grant lookup writes LastSeenAt.
    /// </summary>
    [Fact]
    public async Task ResolvesTheCallerOnceForOneDecision()
    {
        var caller = new StubCallerIdentity(Grant(Person(isAdmin: true)));

        await NewGate(caller).EvaluateAsync("GET", "/apps/admin/", null, acceptScope: null, default);

        Assert.Equal(1, caller.Asked);
    }

    private static AdminGate NewGate(ICallerIdentity caller, bool enabled = true, bool enforceAdmin = true) =>
        new(caller, Options.Create(new AuthOptions { Enabled = enabled, EnforceAdmin = enforceAdmin }), NullLogger<AdminGate>.Instance);

    private static EfPerson Person(bool isAdmin) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Ada",
        IsAdmin = isAdmin,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static EfAuthGrant Grant(EfPerson? person) => new()
    {
        Id = Guid.NewGuid(),
        TokenHash = new byte[AuthHash.Length],
        Label = "Ada's iPhone",
        Kind = AuthGrantKind.Interactive,
        CreatedAt = DateTimeOffset.UnixEpoch,
        CookieIssuedAt = DateTimeOffset.UnixEpoch,
        PersonId = person?.Id,
        Person = person,
    };

    /// <summary>
    /// Answers with one grant, or none, and counts the asking - which is how a
    /// test proves the dormant path never looks at a credential at all.
    /// </summary>
    private sealed class StubCallerIdentity(EfAuthGrant? grant, EfApiKey? key = null) : ICallerIdentity
    {
        public int Asked { get; private set; }

        public Task<EfAuthGrant?> GrantAsync(CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(grant);
        }

        public Task<Guid?> PersonIdAsync(CancellationToken ct) => Task.FromResult(grant?.PersonId);

        public Task<EfPerson?> PersonAsync(CancellationToken ct) => Task.FromResult(grant?.Person);

        /// <summary>Uncounted: <see cref="Asked"/> is about grant lookups, which are the expensive ones - they write LastSeenAt.</summary>
        public Task<EfApiKey?> ApiKeyAsync(CancellationToken ct) => Task.FromResult(key);

        /// <summary>Always nobody: AdminGate is only awake where the wall is up, and the local lane is only alive where it is down.</summary>
        public Task<Actor?> LocalAsync(CancellationToken ct) => Task.FromResult<Actor?>(null);

        public Task<bool> IsProgramAsync(CancellationToken ct) => Task.FromResult(key is not null);

        public Task<string> ActorNameAsync(CancellationToken ct) =>
            Task.FromResult(grant?.Person?.Name ?? key?.Name ?? CallerIdentity.Unattributed);
    }
}
