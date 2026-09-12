using Hatch.Api.Common;
using Hatch.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// Where the runner comes from: the image that is already running.
///
/// A friend who has the stack up has one artifact and no .NET SDK, so the
/// binary they need is published from the same build as the API serving this
/// route (Dockerfile.api) and handed back from beside it. That is what makes
/// the revision below honest - it is the commit the image was built from, and
/// therefore the commit the downloaded `hatch` was built from too, rather than
/// two versions that happen to be near each other.
///
/// Plain <c>[RequireAdmin]</c> with no <c>AcceptScope</c>, matching
/// <see cref="SettingsController"/> beside it rather than the Hatch-scoped
/// reads elsewhere in the module: the audience is a person at a browser
/// setting up a machine, and nobody's dispatcher needs to download the program
/// it is already running as. Wherever the wall is off - every local install -
/// the guard is dormant and this answers anyone, exactly like the rest of
/// /apps/hatch.
/// </summary>
[ApiController]
[Route("api/hatch/runner")]
[RequireAdmin]
public class RunnerController(IHatchRevision revision, IWebHostEnvironment env) : ControllerBase
{
    /// <summary>
    /// The platforms the image publishes, in the order the page lists them.
    /// The same RIDs as the Makefile's <c>HATCH_RIDS</c> and Dockerfile.api's
    /// loop; a RID added to those without being added here is simply not
    /// offered, which is a missing download rather than a broken page.
    /// </summary>
    private static readonly (string Rid, string Platform, string FileName)[] Platforms =
    [
        ("win-x64", "Windows", "hatch.exe"),
        ("osx-arm64", "macOS (Apple Silicon)", "hatch"),
        ("osx-x64", "macOS (Intel)", "hatch"),
        ("linux-x64", "Linux", "hatch"),
    ];

    /// <summary>Where the image put them. Not under wwwroot/apps, so no static handler serves them around this controller.</summary>
    private const string RunnerDirectory = "hatch-runner";

    /// <summary>
    /// What the Runner page draws, in one call.
    /// </summary>
    /// <remarks>
    /// The revision rides along rather than being fetched from
    /// <c>/api/hatch-revision</c>: it is a fact about the binaries listed
    /// beside it, and a page that asked twice could draw a download list from
    /// one build with a revision from another.
    ///
    /// Only what is actually on disk is listed. A `dotnet run` from a checkout
    /// has published nothing, so the list is empty and the page says there is
    /// nothing to download - which is true, and better than four links that
    /// 404.
    /// </remarks>
    [HttpGet]
    public RunnerDownloadsDto Get() => new(
        revision.Revision,
        Platforms
            .Where(p => System.IO.File.Exists(PathFor(p)))
            .Select(p => new RunnerDownloadDto(p.Rid, p.Platform, p.FileName, $"/api/hatch/runner/download/{p.Rid}"))
            .ToArray());

    /// <summary>
    /// One binary, as an attachment.
    /// </summary>
    /// <remarks>
    /// The RID is looked up against the fixed list above and the path is built
    /// from what that lookup returned, so an unrecognised - or traversing -
    /// argument never reaches <see cref="Path.Combine"/> at all. The check is
    /// the lookup, not a sanitiser.
    /// </remarks>
    [HttpGet("download/{rid}")]
    public ActionResult Download(string rid)
    {
        var platform = Platforms.FirstOrDefault(p => p.Rid == rid);
        if (platform.Rid is null) return NotFound();

        var path = PathFor(platform);
        if (!System.IO.File.Exists(path)) return NotFound();

        // application/octet-stream with the file name: a browser that decided
        // for itself what `hatch` was would be a browser that rendered it.
        return PhysicalFile(path, "application/octet-stream", platform.FileName);
    }

    private string PathFor((string Rid, string Platform, string FileName) platform) =>
        Path.Combine(env.WebRootPath, RunnerDirectory, platform.Rid, platform.FileName);
}

/// <summary>
/// What the Runner page draws: which platforms this image can hand out, and
/// which commit all of them - and it - were built from.
/// </summary>
/// <remarks>
/// Not <c>RunnerDto</c>, which is one running loop on the board
/// (<see cref="RunnersController"/>). Everywhere else in Hatch a runner is a
/// process - the thing a claim names, the thing <c>HATCH_RUNNER</c> renames -
/// so the word belongs to that, and this is the downloads beside it.
/// </remarks>
/// <param name="Revision">The full sha stamped into this build, or "dev" for one nothing stamped.</param>
/// <param name="Downloads">In platform order, and only the ones present. Empty on a build that published none.</param>
public record RunnerDownloadsDto(string Revision, RunnerDownloadDto[] Downloads);

/// <summary>
/// One platform's binary.
/// </summary>
/// <param name="Rid">The .NET runtime identifier, which is also what the page's own detection answers.</param>
/// <param name="Platform">What to call it to a person.</param>
/// <param name="FileName">What it lands on disk as - `hatch.exe` on Windows, `hatch` everywhere else.</param>
/// <param name="Url">Where to get it. Named by the server so the page never builds a path of its own.</param>
public record RunnerDownloadDto(string Rid, string Platform, string FileName, string Url);
