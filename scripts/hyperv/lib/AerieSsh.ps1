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

function ConvertTo-NormalizedSshPublicKey {
    <#
    .SYNOPSIS
        Reduces an OpenSSH public key to the two fields that decide its
        identity - algorithm and base64 blob - so that two spellings of the
        same key compare equal.

    .DESCRIPTION
        The trailing comment is free text and routinely differs between
        sources: the same key is 'ssh-ed25519 AAAA... nathan@laptop' in a
        GitHub variable and bare 'ssh-ed25519 AAAA...' out of
        `ssh-keygen -y`. Comparing the raw strings would report those as
        different keys and send the reader hunting a mismatch that isn't one.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$PublicKey)

    $fields = @($PublicKey.Trim() -split '\s+' | Where-Object { $_ })
    if ($fields.Count -lt 2) { return $null }
    "$($fields[0]) $($fields[1])"
}

function Get-SshPublicKeyFingerprint {
    <#
    .SYNOPSIS
        Returns the SHA256 fingerprint of an OpenSSH public key, or $null if
        ssh-keygen won't parse it.

    .DESCRIPTION
        Doubles as the only trustworthy syntax check on a public key. The
        cheap regex elsewhere anchors the start of the string and so accepts
        a key that was line-wrapped in transit (a common outcome of pasting
        one into a GitHub Actions variable from a terminal that hard-wrapped
        it) - which then renders into user-data as a broken YAML scalar and
        produces a VM that rejects the key it was built with. ssh-keygen
        either parses the whole thing or it doesn't.

        Fingerprints are safe to print: they're derived from the public half
        and are the join key between this run's logs, the seed ISO, and the
        `cloud-init` authorized-keys banner on the VM's serial console.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$PublicKey)

    $tempFile = Join-Path $env:TEMP "aerie-pub-$([Guid]::NewGuid().ToString('N')).pub"
    try {
        [IO.File]::WriteAllText($tempFile, ($PublicKey.Replace("`r`n", "`n").Trim() + "`n"))
        $ErrorActionPreference = 'Continue'
        $output = & ssh-keygen.exe -l -f $tempFile 2>&1
        if ($LASTEXITCODE -ne 0) { return $null }
        # "256 SHA256:xxxxx comment (ED25519)"
        return ((([string]$output) -split '\s+') | Where-Object { $_ -like 'SHA256:*' } | Select-Object -First 1)
    }
    finally {
        Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
    }
}

function Resolve-SshPrivateKeyFile {
    <#
    .SYNOPSIS
        Materializes the private key to verify with (writing key *content* to
        a locked-down temp file if that's what was given), confirms ssh.exe
        can parse it, and - given -ExpectedPublicKey - confirms it is the
        private half of the key about to be baked into the VM. All before any
        VM work starts.

    .DESCRIPTION
        A malformed or truncated key (e.g. a mangled NODE_SSH_PRIVATE_KEY
        secret) otherwise surfaces only after a full golden-image build and
        VM boot, as a generic "Permission denied" from Wait-AerieNodeReady -
        which that function then attributes to a stale authorized_keys on a
        resumed VM, since that's the far more common cause of that message.
        Validating the key's format up front, before it's ever used, means a
        bad key fails in seconds with a message that actually points at the
        key instead of at the wrong theory.

        The pairing check exists for the same reason and costs nothing: this
        function already had to run `ssh-keygen -y` to prove the key parses,
        and that command's whole output is the public half. Comparing it to
        the key being injected turns "the two secrets don't correspond" from
        a 45-minute round trip ending in an ambiguous auth failure into a
        preflight error naming both fingerprints.
    #>
    [CmdletBinding()]
    param(
        [string]$SshPrivateKey,
        [string]$SshPrivateKeyPath,
        [string]$ExpectedPublicKey,
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
    #
    # Function-scoped drop to Continue, as in Invoke-NodeSsh: callers set
    # $ErrorActionPreference to Stop, under which the 2>&1 redirect below
    # turns anything ssh-keygen writes to stderr into a terminating
    # NativeCommandError - so a rejected key would surface as that generic
    # error rather than the message written for it a few lines down.
    $ErrorActionPreference = 'Continue'
    $keygenOutput = '' | & ssh-keygen.exe -y -f $path 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
        throw "The SSH private key at '$path' doesn't parse (ssh-keygen: $keygenOutput). Check -SshPrivateKey / -SshPrivateKeyPath (or the NODE_SSH_PRIVATE_KEY secret) holds a complete, unencrypted OpenSSH private key with its BEGIN/END markers intact - not truncated, not the public key, not passphrase-protected."
    }

    # -y writes the public half to stdout; that's the thing worth comparing,
    # not just the exit code.
    $derivedPublicKey = (@($keygenOutput) -join "`n").Trim()
    $derivedFingerprint = Get-SshPublicKeyFingerprint -PublicKey $derivedPublicKey

    if ($ExpectedPublicKey) {
        $expectedFingerprint = Get-SshPublicKeyFingerprint -PublicKey $ExpectedPublicKey
        if (-not $expectedFingerprint) {
            if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
            throw "The SSH public key (-SshPublicKey / NODE_SSH_PUBLIC_KEY) isn't something ssh-keygen can parse. The most common cause is a line break introduced when the key was pasted - it must be a single line, 'ssh-ed25519 AAAA... [comment]'. Left as-is it renders into the seed ISO's user-data as a broken YAML scalar, and the VM ends up rejecting the very key it was built with."
        }

        if ((ConvertTo-NormalizedSshPublicKey $derivedPublicKey) -ne (ConvertTo-NormalizedSshPublicKey $ExpectedPublicKey)) {
            if ($tempKeyFile) { Remove-Item $tempKeyFile -Force -ErrorAction SilentlyContinue }
            throw @"
The SSH keypair doesn't match. The public key about to be baked into this VM is not the public half of the private key this run would verify with, so the VM could only ever refuse it.

  public  (-SshPublicKey  / NODE_SSH_PUBLIC_KEY):  $expectedFingerprint
  private (-SshPrivateKey / NODE_SSH_PRIVATE_KEY): $derivedFingerprint

Fix whichever is wrong: 'ssh-keygen -y -f <private key>' prints the public half that belongs with the private key you're using.
"@
        }
    }

    [pscustomobject]@{ Path = $path; TempFile = $tempKeyFile; Fingerprint = $derivedFingerprint }
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

    .PARAMETER StdIn
        Piped to the remote command's standard input. This is how anything
        sensitive should reach a node: sshd runs the command through a login
        shell, so -Command lands in that shell's argv, where any other local
        user's `ps` can read it for as long as the command runs. Standard
        input never appears there. Callers pass the shape of the work in
        -Command and the secret bytes in -StdIn.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$IPAddress,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$KeyPath,
        [Parameter(Mandatory)][string]$KnownHostsFile,
        [Parameter(Mandatory)][string]$Command,
        [string]$StdIn,
        [int]$ConnectTimeoutSec = 10
    )

    # Windows PowerShell 5.1's native-command argument binder wraps an argument
    # containing whitespace in double quotes but does not escape the double
    # quotes already inside it, so ssh.exe's own CommandLineToArgvW parsing
    # removes every one of them. The remote shell then runs a script that is
    # subtly not the one that was written, with no error anywhere: `tr -d
    # "\r\n"` arrives as `tr -d rn`, which silently deletes every r and n from
    # whatever it was filtering. Nothing downstream can tell that happened, so
    # it is refused here rather than diagnosed later from its consequences.
    #
    # Quote with ' in remote scripts. Where a value genuinely needs " (or must
    # not be word-split), send it on -StdIn instead - which is where anything
    # sensitive belongs anyway.
    if ($Command.Contains('"')) {
        throw @"
Invoke-NodeSsh was given a -Command containing a double quote, which cannot survive the trip to the node.

Windows PowerShell 5.1 does not escape embedded double quotes when it builds ssh.exe's command line, and ssh.exe's argument parsing then strips them - so the remote shell would run the script with every " deleted, silently and without error.

Rewrite the command using single quotes, or pass the value that needs quoting on -StdIn. The offending command was:

$Command
"@
    }

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

        # -StdIn is a *text* channel whose encoding this function does not
        # control, and callers must treat it as one. Read the next paragraph
        # before sending anything whose exact bytes matter.
        #
        # On this repo's own runner, a string piped through here arrives on the
        # node with a UTF-8 BOM - EF BB BF - welded to its front. That cost a
        # Flux bootstrap: the PAT reached the cluster three bytes too long,
        # preflight passed because preflight encodes the in-process string and
        # never touches this pipe, and GitHub answered 401 to a demonstrably
        # correct credential 15 minutes later inside source-controller, where
        # nothing pointed back here.
        #
        # $OutputEncoding is the documented knob for this and is pinned below
        # to a BOM-less encoding. It is not sufficient: the BOM survived the
        # pin, so on Windows PowerShell 5.1 something other than this variable
        # is emitting the preamble - the StreamWriter .NET Framework builds
        # over the child's stdin from [Console]::InputEncoding is the likely
        # culprit, and that is process-wide state a library function has no
        # business mutating. The pin stays because it is free and correct in
        # its own right; it is just not a guarantee.
        #
        # So: anything that must arrive byte-exact gets base64'd by the caller
        # and decoded on the node, which is immune to every ambient encoding
        # this pipe might acquire rather than a bet on one of them. See
        # Bootstrap-Flux.ps1's token handoff for the pattern.
        #
        # Sync-AerieSecrets.ps1 sends a YAML manifest through here and is left
        # alone deliberately - the YAML spec permits a byte order mark at the
        # start of a stream, so kubectl has always accepted what this pipe
        # hands it. That is luck about the format, not a property of this
        # channel, and it is exactly why the rule above is written as "byte-
        # exact payloads base64" rather than "this pipe is fine".
        $OutputEncoding = New-Object Text.UTF8Encoding($false)

        if ($PSBoundParameters.ContainsKey('StdIn')) {
            $StdIn | & ssh.exe @sshArgs 1> $stdout 2> $stderr
        }
        else {
            & ssh.exe @sshArgs 1> $stdout 2> $stderr
        }
        $exitCode = $LASTEXITCODE

        # "$(...)" rather than [string](...): when the command produced truly
        # empty output, Get-Content -Raw returns nothing at all (zero objects,
        # not one holding $null), and casting that empty pipeline with
        # [string](...) yields $null rather than '' - a quirk in how the cast
        # operator handles an expression with no output, distinct from how it
        # handles a $null value. String interpolation coerces the same empty
        # pipeline to '' correctly, which is what every caller here already
        # assumes StdOut/StdErr can be chained on (.Trim(), string ops) without
        # a null check.
        [pscustomobject]@{
            ExitCode = $exitCode
            StdOut   = "$(Get-Content -Path $stdout -Raw -ErrorAction SilentlyContinue)"
            StdErr   = "$(Get-Content -Path $stderr -Raw -ErrorAction SilentlyContinue)"
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

    # 'done' is not the same as 'did anything'. With no readable datasource
    # cloud-init completes cleanly against DataSourceNone, having applied no
    # user-data at all: no account, no authorized_keys, no packages. The VM
    # still boots, still takes the DHCP-reserved address, and still picks up
    # the right hostname from DHCP option 12, so every signal short of this
    # one says success - and the eventual symptom is an SSH failure that
    # reads as a rejected key. Assert-NoCloudIso should stop a seed like that
    # ever reaching a VM; this is the backstop for the ways it could still
    # happen (DVD drive detached, media swapped, ISO unreadable in-guest).
    if ($final.StdOut -match 'DataSourceNone') {
        throw @"
cloud-init finished on $IPAddress but found no datasource (DataSourceNone), so none of the seed ISO's user-data was applied - the '$User' account does not exist on this VM, and sshd will reject every key with a generic 'Permission denied (publickey)' that looks exactly like a wrong-key problem.

The seed ISO wasn't readable in-guest. Check the VM still has the cidata DVD attached, and that its root holds files named exactly 'user-data' and 'meta-data'.

$($final.StdOut)
"@
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
        # Which datasource cloud-init actually used, and the fingerprint of
        # every key that reached authorized_keys. Both belong in the
        # acceptance report: they're what makes 'this node is provisioned'
        # checkable after the fact rather than inferred from the run passing.
        "echo '--- cloud-init'; cloud-init status --long 2>/dev/null | grep -i -E 'status:|detail:' || echo 'cloud-init status unavailable'"
        "echo '--- authorized keys'; ssh-keygen -l -f ~/.ssh/authorized_keys 2>/dev/null || echo 'no authorized_keys'"
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
