using Aerie.Api.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System.Reflection;

namespace Aerie.Api.Tests.Auth;

/// <summary>
/// The audit, made executable. Which verbs an administrator alone may take is a
/// judgement about this house rather than a rule a compiler can derive, so it
/// is written down once - here - and the test is that the code agrees with it.
///
/// It exists because the failure mode is silence. A new endpoint added to
/// DevicesController next year is unguarded by default, which is the correct
/// default (see RequireAdminAttribute on why this is opt-in) and also the
/// reason nobody would notice. This turns "I did not think about it" into a
/// red build with the action's name in it, and the fix is to think about it and
/// then edit one of the two lists below.
///
/// The shape of the judgement, for whoever is editing those lists:
///
/// - **Shaping the house is guarded. Operating it is not.** Creating a zone,
///   importing a device, wiring a panel - guarded. Turning a lamp on, nudging a
///   thermostat, triggering a routine, playing music - open, and it has to
///   stay open, because the dashboard runs on a hallway tablet nobody signs in
///   to and the whole promise is that the family never has to.
/// - **Reads stay open, with two exceptions.** Both exceptions are inventories
///   of credentials rather than facts about the house: the session list
///   (AuthController.ListGrants, PeopleController.GetSessions) and the
///   connection settings (SettingsController, DevicesController's camera
///   connection).
/// - **The family modules are not in this file at all.** Quill, Gather,
///   Storage and Game are the household's own apps; their authorization is
///   ownership, expressed as a WHERE clause in the module
///   (docs/auth-architecture.md, "A person is an authorization input"), and a
///   global role has nothing to say about them.
/// - **Hatch is the exception, and it is not really one.** It lives under
///   Modules/ for the schema and the migration history, but it is not a family
///   app - it is the operator's own tooling wearing a module's clothes, like
///   the admin app is the operator's own screen. Every one of its verbs is
///   guarded, reads included, and its bundle 404s for non-admins the same way
///   (AdminAppMiddleware). Phase 6 of docs/plans/pjm.md widens that to scoped
///   API keys, which is a second credential through the same gate rather than
///   a second gate.
/// </summary>
public class AdminSurfaceTests
{
    /// <summary>
    /// Every action an administrator alone may take, as "Controller.Action".
    /// Adding one here without adding the attribute fails, and the reverse
    /// fails too - the list is the audit, not a subset of it.
    /// </summary>
    private static readonly string[] Guarded =
    [
        // Sessions: minting a credential, revoking one, and the inventory of
        // every credential in the household. Redeem, me and sign-out stay open
        // - they are how a device becomes anyone at all.
        "AuthController.ListGrants",
        "AuthController.RevokeGrant",
        "AuthController.LinkGrantPerson",
        "AuthController.CreateInvite",

        // API keys, and the one entry in this file with a second reason. They
        // are credentials, so they belong beside the sessions above - and they
        // carry no AcceptScope, so a key cannot mint a key. Allowing that would
        // make the scope system decorative, since any key could issue itself a
        // second one carrying whatever it liked.
        "ApiKeysController.ListKeys",
        "ApiKeysController.CreateKey",
        "ApiKeysController.RevokeKey",

        // People. The writes above all, because IsAdmin is set here: an
        // unguarded PUT would let any enrolled device promote itself, which
        // makes the whole boundary a formality. The name and the photo stay
        // readable - the family apps render both.
        "PeopleController.Create",
        "PeopleController.Update",
        "PeopleController.Delete",
        "PeopleController.PutPhoto",
        "PeopleController.DeletePhoto",
        "PeopleController.GetSessions",

        // The domain model: zones, devices, channels, panels, routines. Every
        // GET on all five is open.
        "ZonesController.Create",
        "ZonesController.Update",
        "ZonesController.Delete",
        "ZonesController.PutComfort",
        "DevicesController.Create",
        "DevicesController.Update",
        "DevicesController.Delete",
        "DevicesController.AddChannel",
        "DevicesController.UpdateChannel",
        "DevicesController.DeleteChannel",
        "DevicesController.RefreshOptions",
        "DevicesController.Backfill",
        "PanelsController.Create",
        "PanelsController.Update",
        "PanelsController.Delete",
        "RoutinesController.Create",
        "RoutinesController.Update",
        "RoutinesController.Delete",

        // Camera connection: a host, a port, a path and a username - a route
        // straight to the camera that goes around Aerie entirely. Watching the
        // feed is a different question, and CameraController stays open.
        "DevicesController.GetCameraConnection",
        "DevicesController.UpsertCameraConnection",
        "DevicesController.DeleteCameraConnection",

        // Integrations. The OAuth callback is deliberately absent: nobody
        // arrives there by choosing to, and the single-use state row is a
        // stronger claim than a session.
        "CalendarOAuthController.Start",
        "CalendarsController.RefreshCalendars",
        "CalendarsController.Sync",
        "CalendarsController.UpdateCalendar",
        "CalendarsController.DeleteAccount",
        "HomeAssistantController.FetchFromHomeAssistant",
        "DiscoveryController.GetUnmapped",
        "PhotosController.RefreshAlbums",
        "PhotosController.UpdateAlbum",

        // Hatch, whole. Reads included, because the board is a list of what
        // the operator is doing and every card title on it - not a fact about
        // the house that a hallway tablet has any business rendering.
        "BoardController.GetBoard",
        "ProjectsController.GetProjects",
        "ProjectsController.CreateProject",
        "ProjectsController.PatchProject",
        "ProjectsController.DeleteProject",
        "StatusesController.GetStatuses",
        "StatusesController.CreateStatus",
        "StatusesController.PatchStatus",
        "StatusesController.DeleteStatus",
        "IssuesController.GetIssue",
        "IssuesController.CreateIssue",
        "IssuesController.PatchIssue",
        "IssuesController.DeleteIssue",
        "IssuesController.MoveIssue",
        "IssueThreadController.GetComments",
        "IssueThreadController.AddComment",
        "IssueThreadController.GetEvents",
        "ImportController.Preview",
        "ImportController.PreviewText",
        "ImportController.Import",

        // The whole controller, reads included. See SettingsController.
        "SettingsController.GetAll",
        "SettingsController.Get",
        "SettingsController.Upsert",
        "SettingsController.Delete",
    ];

    /// <summary>
    /// Actions that stay open even though a passing glance would guard them,
    /// each with the reason. These are not exempt from the audit - they are its
    /// most considered entries, and the test asserts they are still open so
    /// that guarding one is a deliberate edit rather than a reflex.
    /// </summary>
    private static readonly string[] DeliberatelyOpen =
    [
        // Control, not configuration. The tablet in the hallway does all of
        // these and nobody has ever signed in to it.
        "DevicesController.SetPower",
        "DevicesController.SetSetpoint",
        "DevicesController.SetMode",
        "DevicesController.TriggerScene",
        "DevicesController.PlayMedia",
        "PanelsController.SetPower",
        "PanelsController.SetSetpoint",
        "RoutinesController.Trigger",
        "RoutinesController.TurnOff",

        // How a device becomes anyone at all. Gating these is the infinite
        // redirect loop the wall's own allow-list exists to avoid.
        "AuthController.Verify",
        "AuthController.Redeem",
        "AuthController.Me",
        "AuthController.SignOutDevice",

        // Where Google sends the browser back. Guarded by the single-use state
        // row, which is a stronger claim than "an admin is holding this tab".
        "CalendarOAuthController.Callback",

        // A name and a face, rendered next to a note in the family apps.
        "PeopleController.GetAll",
        "PeopleController.Get",
        "PeopleController.GetPhoto",
    ];

    [Fact]
    public void TheGuardedSurfaceIsExactlyWhatWasAudited()
    {
        var actual = ActionsWhere(guarded: true);

        // Set comparison rather than sequence: the lists above are grouped for
        // a reader, not sorted for a machine.
        Assert.Equal(Guarded.OrderBy(a => a), actual.OrderBy(a => a));
    }

    [Fact]
    public void TheConsideredExceptionsAreStillOpen()
    {
        var open = ActionsWhere(guarded: false).ToHashSet();

        foreach (var action in DeliberatelyOpen)
        {
            Assert.Contains(action, open);
        }
    }

    /// <summary>
    /// Guards the lists themselves: every name in them has to be a real action,
    /// or a rename turns an audited endpoint into an unaudited one and both
    /// tests above keep passing while the entry sits there meaning nothing.
    /// </summary>
    [Fact]
    public void EveryNameInBothListsStillNamesAnAction()
    {
        var all = ActionsWhere(guarded: true).Concat(ActionsWhere(guarded: false)).ToHashSet();

        foreach (var action in Guarded.Concat(DeliberatelyOpen))
        {
            Assert.Contains(action, all);
        }
    }

    private static IEnumerable<string> ActionsWhere(bool guarded) =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsPublic: true } && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Where(m => IsGuarded(m) == guarded)
                .Select(m => $"{t.Name}.{m.Name}"));

    /// <summary>An attribute on the controller covers every action in it - which is how SettingsController is guarded whole.</summary>
    private static bool IsGuarded(MethodInfo action) =>
        action.GetCustomAttribute<RequireAdminAttribute>() is not null
        || action.DeclaringType!.GetCustomAttribute<RequireAdminAttribute>() is not null;
}
