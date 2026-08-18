namespace Aerie.Api.Models.VmConsoleLogs;

/// <summary>One serial console line shipped by scripts/hyperv/lib/Send-VmConsoleLog.ps1.</summary>
public record VmConsoleLogEntry(
    string VmName,
    string Line,
    // $env:COMPUTERNAME of the Hyper-V host running the shipper task - the
    // actual actor making this request, since VmName only identifies the
    // console's source VM, not which physical host shipped it (relevant once
    // there's more than one Hyper-V host).
    string? HostName = null);
