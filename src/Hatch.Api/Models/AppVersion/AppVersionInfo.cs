namespace Hatch.Api.Models.AppVersion;

/// <summary>
/// The identity of the build of a frontend app (wwwroot/apps/&lt;app&gt;) this
/// replica is currently serving. Consumed by long-lived clients - the kiosk
/// dashboard above all, which loads once at tablet boot and would otherwise
/// run whatever bundle was deployed that morning forever.
/// </summary>
/// <param name="App">The app directory name, e.g. "dashboard".</param>
/// <param name="Version">
/// Comparison token: the app's content-hashed asset filenames, deduplicated,
/// sorted and joined with '+'. Deliberately the filenames themselves rather
/// than a digest of them - a client comparing versions can read this straight
/// out of its own DOM without reimplementing a hash, and a drift shows up in
/// the logs as two readable filenames instead of two opaque hex strings.
/// </param>
/// <param name="Assets">The same filenames unjoined, for diagnostics.</param>
public record AppVersionInfo(string App, string Version, IReadOnlyList<string> Assets);
