using Hatch.Api.Common;
using Hatch.Api.Controllers;
using Hatch.Api.Ef;
using Hatch.Api.Models.UiLogs;
using Hatch.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Hatch.Api.Tests.UiLogs;

/// <summary>
/// The relay that turns a browser's log line into one of ours.
///
/// What is asserted here is the *structured* fields rather than the rendered
/// sentence, because those are what a person filters on at logs.&lt;domain&gt;.
/// A field that renders into the message but never becomes a property is a
/// field nobody can search for, which is indistinguishable from a field that is
/// not there.
/// </summary>
public class UiLogsControllerTests
{
    [Fact]
    public void CarriesThePersonBehindTheDeviceOntoTheLine()
    {
        // The whole point of the feature: Adam opens the dashboard on his
        // phone, and the page-load line says so by name rather than by GUID.
        var person = new EfPerson { Id = Guid.NewGuid(), Name = "Adam", CreatedAt = default, UpdatedAt = default };
        var (controller, logger) = NewController(GrantFor(person));

        controller.Post([Entry("dashboard", "Page loaded")]);

        var line = Assert.Single(logger.Lines);
        Assert.Equal(person.Id.ToString(), line["PersonId"]);
        Assert.Equal("Adam", line["PersonName"]);
    }

    [Fact]
    public void NamesTheDeviceByIdAndThePersonByName()
    {
        // Opposite treatments, on purpose. A grant's Label is free text an
        // administrator typed, so only its id travels - free text written into
        // a field people will later filter on is a field that cannot be
        // filtered on. A person's name went through PersonName, so it is a
        // value rather than a note, and it is the half a human can read.
        var person = new EfPerson { Id = Guid.NewGuid(), Name = "Adam", CreatedAt = default, UpdatedAt = default };
        var grant = GrantFor(person);
        var (controller, logger) = NewController(grant);

        controller.Post([Entry("dashboard", "Page loaded")]);

        var line = Assert.Single(logger.Lines);
        Assert.Equal(grant.Id.ToString(), line["ActorId"]);
        Assert.DoesNotContain("Kitchen tablet", line.Values.Select(v => v?.ToString()));
    }

    [Fact]
    public void LeavesThePersonFieldsEmptyForADeviceNobodyHasClaimed()
    {
        // The hallway tablet belongs to the house. Not a degraded line - it
        // still carries the device - and not a reason to refuse the batch.
        var (controller, logger) = NewController(GrantFor(person: null));

        controller.Post([Entry("kiosk", "Page loaded")]);

        var line = Assert.Single(logger.Lines);
        Assert.Null(line["PersonId"]);
        Assert.Null(line["PersonName"]);
    }

    [Fact]
    public void StillShipsLogsFromABrowserThatIsNotSignedIn()
    {
        // The sign-in shell's own lines. A relay that required a grant would
        // drop exactly the logs someone debugging a locked-out device needs.
        var (controller, logger) = NewController(grant: null);

        var result = controller.Post([Entry("auth", "Redemption failed")]);

        Assert.IsType<NoContentResult>(result);
        var line = Assert.Single(logger.Lines);
        Assert.Null(line["ActorId"]);
        Assert.Null(line["PersonId"]);
    }

    [Fact]
    public void KeepsTheAppItselfInTheServiceFieldRatherThanTheRelay()
    {
        // Guarding the field fluent-bit's service_tag.lua reads to override the
        // container-level default - without it every browser line would file
        // itself under hatch-api.
        var (controller, logger) = NewController(GrantFor(person: null));

        controller.Post([Entry("admin", "Saved a person")]);

        Assert.Equal("admin", Assert.Single(logger.Lines)["Service"]);
    }

    private static UiLogEntry Entry(string app, string message) =>
        new(app, "info", message, null, "session-1", "https://home.example.com/", null);

    private static EfAuthGrant GrantFor(EfPerson? person) => new()
    {
        Id = Guid.NewGuid(),
        TokenHash = new byte[32],
        Label = "Kitchen tablet",
        Kind = AuthGrantKind.Device,
        CreatedAt = default,
        CookieIssuedAt = default,
        PersonId = person?.Id,
        Person = person,
    };

    private static (UiLogsController Controller, CapturingLogger Logger) NewController(EfAuthGrant? grant)
    {
        var logger = new CapturingLogger();
        var httpContext = new DefaultHttpContext();
        if (grant is not null) httpContext.SetAuthGrant(grant);

        var controller = new UiLogsController(logger, new StubRevision())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (controller, logger);
    }

    /// <summary>
    /// Keeps each line's structured state, which is the thing under test - the
    /// rendered message is what a human reads once and the properties are what
    /// every query in OpenSearch is written against.
    /// </summary>
    private sealed class CapturingLogger : ILogger<UiLogsController>
    {
        public List<Dictionary<string, object?>> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Lines.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    private sealed class StubRevision : IHatchRevision
    {
        public string Revision => "abc1234";

        public int Sequence => 1;

        public DateTimeOffset? BuiltAt => null;

        public bool IsDevelopment => false;
    }
}
