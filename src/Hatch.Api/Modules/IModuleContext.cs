using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Hatch.Api.Modules;

/// <summary>
/// Marks a module's <c>DbContext</c> so the startup migration loop in Program.cs
/// can enumerate every module without naming any of them. Each module owns its
/// own Postgres schema and its own migration history table, so migrating one
/// never touches another's model - or the home-automation domain's.
/// </summary>
/// <remarks>
/// <c>Database</c> is already a public member of <c>DbContext</c>, so implementing
/// this interface costs a module context exactly one <c>, IModuleContext</c> and no
/// method bodies. It's declared here (rather than leaving this a bare marker and
/// casting at the call site) so the migration loop stays type-safe.
/// </remarks>
public interface IModuleContext
{
    DatabaseFacade Database { get; }
}
