namespace Aerie.Api.Models.VmConsoleLogs;

/// <summary>One serial console line shipped by scripts/hyperv/lib/Send-VmConsoleLog.ps1.</summary>
public record VmConsoleLogEntry(string VmName, string Line);
