using System.Security.Cryptography;
using System.Text;
using Aerie.Api.Common;
using Aerie.Api.Models.VmConsoleLogs;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

/// <summary>
/// Receives Hyper-V VM serial console lines from
/// scripts/hyperv/lib/Send-VmConsoleLog.ps1 (one Scheduled Task per VM, on
/// the Hyper-V host - see New-AerieVM.ps1) and writes them through the normal
/// ILogger pipeline, so they flow into the existing docker.* -> fluent-bit ->
/// OpenSearch aerie-logs index alongside every other Aerie.Api log line,
/// visible at logs.&lt;domain&gt; under service vm-console.&lt;VMName&gt;
/// (see service_tag.lua's State.Service override).
///
/// Unlike UiLogsController, this isn't same-origin browser traffic - it's
/// server-to-server from Hyper-V hosts across the LAN - so it's gated on a
/// shared-secret header instead of relying on same-origin trust.
/// </summary>
[ApiController]
[Route("api/vm-console-logs")]
public class VmConsoleLogsController(ILogger<VmConsoleLogsController> logger, ISecrets secrets) : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] VmConsoleLogEntry[] entries, [FromHeader(Name = "X-Vm-Log-Token")] string? token)
    {
        var expectedToken = secrets.GetSecret("vm_log_shipper_token");
        if (string.IsNullOrEmpty(expectedToken) || !FixedTimeEquals(token, expectedToken))
        {
            return Unauthorized();
        }

        foreach (var entry in entries)
        {
            logger.LogInformation("{Service} {Line}", $"vm-console.{entry.VmName}", entry.Line);
        }

        return NoContent();
    }

    private static bool FixedTimeEquals(string? actual, string expected)
    {
        if (actual is null)
        {
            return false;
        }

        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        // Lengths almost never match for a wrong token, but comparing them
        // up front (rather than padding to compare in constant time) leaks
        // only the token's length, not any of its content.
        return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
