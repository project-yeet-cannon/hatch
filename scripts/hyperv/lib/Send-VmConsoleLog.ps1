<#
.SYNOPSIS
    Drains a Hyper-V VM's COM1 named pipe and ships each line to Aerie.Api's
    /api/vm-console-logs, so it flows into the existing fluent-bit ->
    OpenSearch pipeline instead of (or in addition to) vmconnect.

.DESCRIPTION
    Registered as a per-VM Scheduled Task by Register-VmConsoleLogShipper.ps1
    (called from New-AerieVM.ps1) - not meant to be run standalone except for
    debugging a shipper that isn't delivering lines.

    Hyper-V's worker process owns the named pipe as its server side only
    while the VM is running (see New-AerieVM.ps1's Set-VMComPort call), so
    this reconnects in a loop rather than treating a connect failure or a
    mid-stream disconnect (VM stopped/recreated) as fatal.
#>
param(
    [Parameter(Mandatory)][string]$VMName,
    [Parameter(Mandatory)][string]$PipeName,
    [Parameter(Mandatory)][string]$IngestUrl,
    [Parameter(Mandatory)][string]$Token
)

$ErrorActionPreference = 'Stop'

# Batched rather than one HTTP call per line - a chatty boot (kernel +
# cloud-init) can produce hundreds of lines a second.
$FlushEvery = 20
$FlushIntervalSeconds = 5

Add-Type -AssemblyName System.Net.Http
$httpClient = [System.Net.Http.HttpClient]::new()
$httpClient.DefaultRequestHeaders.Add('X-Vm-Log-Token', $Token)
$httpClient.Timeout = [TimeSpan]::FromSeconds(10)

function Send-LineBatch {
    param([string[]]$Lines)

    if ($Lines.Count -eq 0) { return }

    # Built by joining individually-serialized entries rather than
    # `$entries | ConvertTo-Json`, which on a single-element array collapses
    # to a bare JSON object instead of a one-element array on Windows
    # PowerShell 5.1.
    $entries = $Lines | ForEach-Object {
        [PSCustomObject]@{ vmName = $VMName; line = $_ } | ConvertTo-Json -Compress
    }
    $json = '[' + ($entries -join ',') + ']'

    try {
        $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
        $response = $httpClient.PostAsync($IngestUrl, $content).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            Write-Warning "vm-console-logs POST returned $($response.StatusCode)"
        }
    }
    catch {
        Write-Warning "vm-console-logs POST failed: $($_.Exception.Message)"
    }
}

while ($true) {
    $pipe = $null
    $reader = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::In)
        $pipe.Connect(30000)
        $reader = [System.IO.StreamReader]::new($pipe)

        $buffer = [System.Collections.Generic.List[string]]::new()
        $lastFlush = Get-Date
        while ($pipe.IsConnected) {
            # Blocks until a line is available or the pipe breaks - fine
            # here since flushing on a timer would otherwise need a second
            # thread just to notice the pipe went away.
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            $buffer.Add($line)

            if ($buffer.Count -ge $FlushEvery -or ((Get-Date) - $lastFlush).TotalSeconds -ge $FlushIntervalSeconds) {
                Send-LineBatch -Lines $buffer
                $buffer.Clear()
                $lastFlush = Get-Date
            }
        }
        if ($buffer.Count -gt 0) { Send-LineBatch -Lines $buffer }
    }
    catch {
        Write-Warning "console pipe '$PipeName' error: $($_.Exception.Message)"
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($pipe) { $pipe.Dispose() }
    }
    Start-Sleep -Seconds 5
}
