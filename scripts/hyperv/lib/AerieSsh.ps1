<#
.SYNOPSIS
    SSH helpers for verifying a freshly provisioned Aerie node from the
    Hyper-V host, using Windows' built-in OpenSSH client.

.NOTES
    Dot-sourced by Initialize-AerieNode.ps1. Nothing here is Hyper-V-specific;
    it only needs ssh.exe on PATH and a private key whose public half was
    injected via cloud-init.
#>

function Assert-OpenSshClient {
    <#
    .SYNOPSIS
        Fails early with an actionable message if ssh.exe or ssh-keygen.exe
        isn't installed.
    #>
    $missing = @('ssh.exe', 'ssh-keygen.exe') | Where-Object { -not (Get-Command $_ -ErrorAction SilentlyContinue) }
    if ($missing) {
        throw @"
$($missing -join ', ') not found on PATH. The OpenSSH client is an optional Windows feature; install it with:

  Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0

Or re-run with -SkipWaitForReady to provision the VM without post-boot verification.
"@
    }
}

function Resolve-SshPrivateKeyFile {
    <#
    .SYNOPSIS
        Materializes the private key to verify with (writing key *content* to
        a locked-down temp file if that's what was given) and confirms
        ssh.exe can actually parse it, before any VM work starts.

    .DESCRIPTION
        A malformed or truncated key (e.g. a mangled NODE_SSH_PRIVATE_KEY
        secret) otherwise surfaces only after a full golden-image build and
        VM boot, as a generic "Permission denied" from Wait-AerieNodeReady -
        which that function then attributes to a stale authorized_keys on a
        resumed VM, since that's the far more common cause of that message.
        Validating the key's format up front, before it's ever used, means a
        bad key fails in seconds with a message that actually points at the
        key instead of at the wrong theory.
    #>
    [CmdletBinding()]
    param(
        [string]$SshPrivateKey,
        [string]$SshPrivateKeyPath,
        [Parameter(Mandatory)][string]$VMName
    )

    if ($SshPrivateKey) {
        $tempKeyFile = Join-Path $env:TEMP "aerie-$VMName-$([Guid]::NewGuid().ToString('N')).key"
        # WriteAllText rather than Set-Content, and LF rather than CRLF:
        # OpenSSH rejects a key file with CRLF line endings, and equally
        # rejects one whose PEM footer has no trailing newline at all.
        # Set-Content would get both wrong. GitHub also strips the trailing
        # newline from multi-line secrets, hence re-adding it.
        [IO.File]::WriteAllText($tempKeyFile, ($SshPrivateKey.Replace("`r`n", "`n").TrimEnd() + "`n"))
        Protect-PrivateKeyFile -Path $tempKeyFile
        $path = $tempKeyFile
    }
    else {
        $path = $SshPrivateKeyPath
        $tempKeyFile = $null
    }

    # Stdin is fed an empty line rather than left attached to the console: an
    # encrypted key would otherwise make ssh-keygen block on a passphrase
    # prompt that nothing here will ever answer.
    $keygenOutput = '' | & ssh-keygen.exe -y -f $path 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
        throw "The SSH private key at '$path' doesn't parse (ssh-keygen: $keygenOutput). Check -SshPrivateKey / -SshPrivateKeyPath (or the NODE_SSH_PRIVATE_KEY secret) holds a complete, unencrypted OpenSSH private key with its BEGIN/END markers intact - not truncated, not the public key, not passphrase-protected."
    }

    [pscustomobject]@{ Path = $path; TempFile = $tempKeyFile }
}

function Protect-PrivateKeyFile {
    <#
    .SYNOPSIS
        Locks a private key file's ACL down to the current user only.

    .DESCRIPTION
        Windows OpenSSH refuses to use a key file that other principals can
        read ("UNPROTECTED PRIVATE KEY FILE"), and a file written into a
        temp/workspace directory inherits ACLs that trip that check. Unlike
        POSIX there's no chmod 600 equivalent, so this strips inheritance and
        grants exactly one ACE.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateScript({ Test-Path $_ -PathType Leaf })]
        [string]$Path
    )

    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $acl = Get-Acl -Path $Path
    $acl.SetAccessRuleProtection($true, $false)   # protect from inheritance, don't copy inherited ACEs
    foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRule($rule) | Out-Null }
    $acl.SetAccessRule(
        (New-Object Security.AccessControl.FileSystemAccessRule($me, 'FullControl', 'Allow'))
    )
    Set-Acl -Path $Path -AclObject $acl
}

function Test-TcpPort {
    <#
    .SYNOPSIS
        Single non-blocking TCP connect attempt. Cheaper and quieter than
        Test-NetConnection, which we'd otherwise be calling in a tight poll.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$IPAddress,
        [int]$Port = 22,
        [int]$TimeoutMs = 3000
    )

    $client = New-Object Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($IPAddress, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) { return $false }
        $client.EndConnect($async)
        return $true
    }
    catch { return $false }
    finally { $client.Close() }
}

function Invoke-NodeSsh {
    <#
    .SYNOPSIS
        Runs one command on the node over SSH and returns its exit code and
        combined output, without throwing on failure.

    .DESCRIPTION
        Callers here poll a machine that is expected to be intermittently
        unreachable (it reboots itself mid-provision), so a non-zero exit is
        an ordinary result, not an error - hence returning the exit code
        rather than throwing.

        Two Windows PowerShell 5.1 landmines are avoided deliberately:
        `$ErrorActionPreference` is dropped to Continue for the call, because
        under Stop the native stderr redirect below raises a terminating
        NativeCommandError; and ssh is invoked with the call operator rather
        than Start-Process, whose -ArgumentList flattens an array by joining
        on spaces without re-quoting, which would shred a multi-line -Command.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$IPAddress,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$KeyPath,
        [Parameter(Mandatory)][string]$KnownHostsFile,
        [Parameter(Mandatory)][string]$Command,
        [int]$ConnectTimeoutSec = 10
    )

    $stdout = [IO.Path]::GetTempFileName()
    $stderr = [IO.Path]::GetTempFileName()
    try {
        $sshArgs = @(
            '-i', $KeyPath
            '-o', 'BatchMode=yes'                       # never prompt for a passphrase; fail instead
            '-o', 'StrictHostKeyChecking=accept-new'    # pin on first sight, still detect later changes
            '-o', "UserKnownHostsFile=$KnownHostsFile"
            '-o', "ConnectTimeout=$ConnectTimeoutSec"
            '-o', 'LogLevel=ERROR'
            "$User@$IPAddress"
            $Command
        )

        # Function-scoped, so it doesn't leak back to the caller.
        $ErrorActionPreference = 'Continue'
        & ssh.exe @sshArgs 1> $stdout 2> $stderr
        $exitCode = $LASTEXITCODE

        [pscustomobject]@{
            ExitCode = $exitCode
            StdOut   = [string](Get-Content -Path $stdout -Raw -ErrorAction SilentlyContinue)
            StdErr   = [string](Get-Content -Path $stderr -Raw -ErrorAction SilentlyContinue)
        }
    }
    finally {
        Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
    }
}

function Get-SshPermanentFailureReason {
    <#
    .SYNOPSIS
        Classifies a failed SSH attempt's stderr as permanent (retrying won't
        help) or transient (the ordinary "node is mid-reboot" case), and
        returns a human-readable reason for the former or $null for the
        latter.

    .DESCRIPTION
        Wait-AerieNodeReady polls a node that is expected to be intermittently
        unreachable while it reboots, so by default any SSH failure is
        treated as "still rebooting" and retried until the timeout. That's
        wrong for a rejected key or a host-key mismatch: neither will ever
        resolve itself by waiting, and burning the full timeout on one just
        turns a one-line diagnosis into a 45-minute round trip.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$StdErr)

    if ($StdErr -match 'Permission denied') {
        return "the key offered wasn't accepted (Permission denied). This will not resolve by waiting."
    }
    if ($StdErr -match 'Host key verification failed') {
        return 'the host key was rejected (Host key verification failed). This will not resolve by waiting.'
    }
    return $null
}

function Wait-AerieNodeReady {
    <#
    .SYNOPSIS
        Blocks until a newly provisioned node has finished cloud-init,
        including the reboot cloud-init triggers on itself, then returns a
        report object describing what actually came up.

    .DESCRIPTION
        "The VM started" is close to meaningless as a success signal here:
        user-data.tmpl.yaml sets power_state.mode = reboot, so the node comes
        up, provisions, and drops off the network again. Naively waiting for
        port 22 reports success during the pre-reboot window.

        So this tracks /proc/sys/kernel/random/boot_id — a fresh value on
        every boot. It records the id seen on first contact and waits for it
        to change, which is the reboot, and only then runs
        `cloud-init status --wait`.

        The fallback exists because the boot we first reach isn't guaranteed
        to be the first one: if the host is slow to hand this script a turn,
        the reboot may already be behind us, and waiting for a boot_id change
        that will never come would hang until timeout. So a node whose
        cloud-init has reported `done` continuously for StableSeconds without
        rebooting is accepted as already-past-the-reboot.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$IPAddress,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$KeyPath,
        [Parameter(Mandatory)][string]$KnownHostsFile,
        [Parameter(Mandatory)][string]$ExpectedHostname,
        [int]$TimeoutMinutes = 45,
        [int]$PollSeconds = 10,
        [int]$StableSeconds = 120
    )

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $ssh = @{
        IPAddress = $IPAddress; User = $User; KeyPath = $KeyPath; KnownHostsFile = $KnownHostsFile
    }

    Write-Host "Waiting for $IPAddress`:22 to answer (first boot) ..."
    while (-not (Test-TcpPort -IPAddress $IPAddress)) {
        if ((Get-Date) -gt $deadline) {
            throw "Timed out after $TimeoutMinutes min waiting for $IPAddress to answer on port 22. Check the DHCP reservation matches the VM's MAC, and watch the console with: vmconnect localhost $ExpectedHostname"
        }
        Start-Sleep -Seconds $PollSeconds
    }
    Write-Host "  port 22 open."

    Write-Host "Waiting for cloud-init to finish and the node to reboot into its new kernel ..."
    $firstBootId = $null
    $doneSince = $null
    $rebooted = $false

    while (-not $rebooted) {
        if ((Get-Date) -gt $deadline) {
            throw "Timed out after $TimeoutMinutes min waiting for cloud-init on $IPAddress. Inspect /var/log/cloud-init-output.log on the node, or watch the console with: vmconnect localhost $ExpectedHostname"
        }

        $probe = Invoke-NodeSsh @ssh -Command 'cat /proc/sys/kernel/random/boot_id; cloud-init status || true'
        if ($probe.ExitCode -ne 0) {
            $permanentReason = Get-SshPermanentFailureReason -StdErr $probe.StdErr
            if ($permanentReason) {
                throw "SSH to $IPAddress as '$User' failed: $permanentReason If you're resuming a VM from an earlier run, remember its authorized_keys was baked in by cloud-init at creation time - a key rotated since then (e.g. a new -SshPublicKey / NODE_SSH_PUBLIC_KEY) never reaches an already-created VM. Confirm the private key matches what was actually baked in, or remove and recreate the VM.`n$($probe.StdErr)"
            }
            # Expected while the node is mid-reboot, and on the very first
            # attempts while sshd is still coming up.
            Write-Host "  node unreachable (likely rebooting) ..."
            Start-Sleep -Seconds $PollSeconds
            continue
        }

        $lines = @(($probe.StdOut -split "`r?`n") | Where-Object { $_.Trim() })
        if ($lines.Count -eq 0) {
            # ssh exited 0 but said nothing - sshd is up while the rest of the
            # system still isn't. Not an error, just too early.
            Start-Sleep -Seconds $PollSeconds
            continue
        }

        $bootId = $lines[0].Trim()
        $status = ($lines | Where-Object { $_ -match 'status:' }) -join ' '
        $shortId = if ($bootId.Length -ge 8) { $bootId.Substring(0, 8) } else { $bootId }

        if (-not $firstBootId) {
            $firstBootId = $bootId
            Write-Host "  reached boot $shortId; $status"
        }
        elseif ($bootId -ne $firstBootId) {
            Write-Host "  rebooted into boot $shortId."
            $rebooted = $true
            break
        }

        if ($status -match 'done') {
            if (-not $doneSince) { $doneSince = Get-Date }
            elseif (((Get-Date) - $doneSince).TotalSeconds -ge $StableSeconds) {
                Write-Warning "cloud-init has reported 'done' for ${StableSeconds}s with no reboot - treating the post-reboot boot as already reached. If this node was expected to reboot, check power_state in the seed ISO's user-data."
                $rebooted = $true
                break
            }
        }
        else {
            $doneSince = $null
        }

        Start-Sleep -Seconds $PollSeconds
    }

    Write-Host "Waiting for cloud-init to settle on the post-reboot boot ..."
    $final = Invoke-NodeSsh @ssh -Command 'sudo cloud-init status --wait --long 2>&1 | tail -n 20' -ConnectTimeoutSec 30

    # The status line is the authority here, not the exit code: cloud-init
    # exits 2 for "degraded done", meaning a module failed but provisioning
    # completed. That deserves a warning, not a failed run - whereas 255 is
    # ssh's own failure code and means we never got an answer at all.
    if ($final.ExitCode -eq 255) {
        $permanentReason = Get-SshPermanentFailureReason -StdErr $final.StdErr
        if ($permanentReason) {
            throw "SSH to $IPAddress as '$User' failed: $permanentReason If you're resuming a VM from an earlier run, remember its authorized_keys was baked in by cloud-init at creation time - a key rotated since then (e.g. a new -SshPublicKey / NODE_SSH_PUBLIC_KEY) never reaches an already-created VM. Confirm the private key matches what was actually baked in, or remove and recreate the VM.`n$($final.StdErr)"
        }
        throw "Lost the SSH connection to $IPAddress while waiting for cloud-init:`n$($final.StdErr)"
    }
    if ($final.StdOut -notmatch 'status:\s*done') {
        throw "cloud-init did not reach 'done' on $IPAddress (exit $($final.ExitCode)). Inspect /var/log/cloud-init-output.log on the node:`n$($final.StdOut)$($final.StdErr)"
    }
    if ($final.ExitCode -ne 0) {
        Write-Warning "cloud-init finished but reports a degraded run (exit $($final.ExitCode)) - a module failed without aborting provisioning. Review /var/log/cloud-init-output.log before treating this node as ready:`n$($final.StdOut)"
    }
    Write-Host "  cloud-init: done."

    # --- collect the acceptance report ---

    # Single line, single quotes only. Windows PowerShell 5.1 does not escape
    # double quotes embedded in an argument when it builds a native process's
    # command line, so any " here would arrive at the remote shell mangled;
    # newlines in an argument are similarly unreliable. Joining with ';' and
    # sticking to ' sidesteps both.
    $probeScript = @(
        # Backtick, not backslash: PowerShell's escape character. Without it
        # $PRETTY_NAME would interpolate to empty here instead of reaching
        # the remote shell.
        "echo '--- identity'; hostnamectl --static; hostname -f; . /etc/os-release; echo `$PRETTY_NAME; uname -r"
        "echo '--- network'; ip -4 -brief addr show scope global; ip route | grep default"
        "echo '--- disks'; lsblk -o NAME,SIZE,TYPE,MOUNTPOINT,FSTYPE"
        "echo '--- time'; timedatectl | sed -n 1,4p; chronyc -n sources 2>/dev/null || echo 'chrony not answering'"
        "echo '--- hyperv'; systemctl is-active hv-kvp-daemon 2>/dev/null || echo 'hv-kvp-daemon not active'"
        "echo '--- uptime'; uptime -p"
    ) -join '; '

    $report = Invoke-NodeSsh @ssh -ConnectTimeoutSec 30 -Command $probeScript

    [pscustomobject]@{
        IPAddress = $IPAddress
        Report    = $report.StdOut
        Rebooted  = $rebooted
    }
}
