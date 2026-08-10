using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Modules;

/// <summary>What the family shell needs from the server that isn't any one module's data.</summary>
/// <param name="PublicBaseUrl">
/// The install's canonical absolute base URL, or null when it isn't configured
/// (or isn't a usable absolute http(s) URL). Null is not an error: the shell
/// falls back to its own origin and says which base it printed, so a fresh
/// operator gets working labels before touching config - just labels that carry
/// whatever host the sheet was printed from.
/// </param>
public record AppsConfigDto(string? PublicBaseUrl);

/// <summary>
/// Platform-level config for the family shell, alongside the module seam rather
/// than inside a module - the first thing to want it is Storage Helper's QR
/// labels, but the value is the install's, not Storage's.
/// </summary>
[ApiController]
[Route("api/apps")]
public class AppsController(IOptions<AppsOptions> options, ILogger<AppsController> logger) : ControllerBase
{
    [HttpGet("config")]
    public AppsConfigDto GetConfig()
    {
        var configured = options.Value.PublicBaseUrl;
        var normalized = AppsOptions.NormalizeBaseUrl(configured);

        // Set-but-unusable is the case worth a log line: unset is the documented
        // default, while a typo'd value is a silent misconfiguration whose only
        // other symptom is a QR code that scans to nothing, on paper, later.
        if (normalized is null && !string.IsNullOrWhiteSpace(configured))
        {
            logger.LogWarning(
                "Apps:PublicBaseUrl {Value} is not an absolute http(s) URL - printed labels will use the printing browser's origin",
                configured);
        }

        return new AppsConfigDto(normalized);
    }
}
