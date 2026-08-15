<#
.SYNOPSIS
    Locates - and, when asked, installs - the third-party tooling an Aerie
    self-hosted runner needs: the AWS CLI v2 and the Windows OpenSSH client.

.DESCRIPTION
    Dot-source this from anything that needs one of those tools:

        . (Join-Path $PSScriptRoot '..\runner\lib\AerieRunnerDependencies.ps1')
        $aws = Get-AerieAwsCliPath

    Two kinds of caller, deliberately split:

      - Provisioning scripts *resolve* (Get-AerieAwsCliPath). They must not
        install: they run under whatever account launched them, which on a
        by-hand run is often not an administrator, and a preflight that tries
        to mutate the machine it's checking is a preflight that can't be
        trusted to report.
      - scripts\runner\Install-RunnerDependencies.ps1 *installs*, as its own
        workflow step, before the provisioning script runs.

    Both live here so the two can't disagree about where a tool is: the
    resolver searches the MSI's install directory as well as PATH, which is
    what makes an install and a use inside the same job work. A Windows
    service only re-reads PATH when it starts, so the runner service's own
    environment is stale until it's restarted - the machine-level PATH entry
    the MSI writes is invisible to the job that triggered the install.

.NOTES
    Windows PowerShell 5.1 compatible - these run on the Hyper-V hosts, which
    have no pwsh.

    Deliberately sets no Set-StrictMode or $ErrorActionPreference at file
    scope, same as hyperv\lib\AerieSsh.ps1 and lib\AerieVersions.ps1: a
    dot-sourced file's top-level statements run in the caller's scope, so
    doing either here would silently change the semantics of whatever script
    dot-sourced it.
#>

. (Join-Path $PSScriptRoot '..\..\lib\AerieVersions.ps1')

# Where the AWS CLI v2 MSI puts aws.exe. Both Program Files spellings are
# checked because a 32-bit PowerShell host (an older runner service, a nested
# WOW64 process) sees $env:ProgramFiles as the x86 directory.
function Get-AerieAwsCliSearchDirectory {
    [CmdletBinding()]
    param()

    $roots = @($env:ProgramW6432, $env:ProgramFiles, ${env:ProgramFiles(x86)}) |
        Where-Object { $_ } |
        Select-Object -Unique

    foreach ($root in $roots) {
        Join-Path $root 'Amazon\AWSCLIV2'
    }
}

function Get-AerieAwsCliPath {
    <#
    .SYNOPSIS
        Returns the full path to aws.exe, or $null if it isn't installed.

    .DESCRIPTION
        PATH first, then the MSI's install directory. The second lookup is not
        redundant: the installer adds itself to the *machine* PATH, which no
        already-running process - including the runner service and every job
        it spawns - picks up until it restarts. Without it, installing the CLI
        and using it in the same run would need a service restart in between.
    #>
    [CmdletBinding()]
    param()

    $onPath = Get-Command aws -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    foreach ($directory in (Get-AerieAwsCliSearchDirectory)) {
        $candidate = Join-Path $directory 'aws.exe'
        if (Test-Path $candidate -PathType Leaf) { return $candidate }
    }

    return $null
}

function Get-AerieAwsCliVersion {
    <#
    .SYNOPSIS
        Returns the version string an aws.exe reports, or $null if it can't be
        read.

    .DESCRIPTION
        `aws --version` prints to stdout on v2 and to stderr on some v1
        builds, so both streams are merged before matching. $null means "there
        is a file there but it doesn't answer like the AWS CLI" - the caller
        treats that as needing an install rather than as a hard failure.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    # Merging stderr into the pipeline raises a terminating NativeCommandError
    # under $ErrorActionPreference = 'Stop' in Windows PowerShell 5.1 - the
    # same reason Invoke-NodeSsh and Invoke-Aws drop to Continue.
    $ErrorActionPreference = 'Continue'
    $output = & $Path --version 2>&1
    if ($LASTEXITCODE -ne 0) { return $null }
    if (($output -join ' ') -match 'aws-cli/(\d+\.\d+\.\d+)') { return $Matches[1] }
    return $null
}

function Test-AerieAdministrator {
    <#
    .SYNOPSIS
        True when the current process can install software.
    #>
    [CmdletBinding()]
    param()

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Publish-AerieToolPath {
    <#
    .SYNOPSIS
        Makes a freshly-installed tool's directory usable now and in the rest
        of the job.

    .DESCRIPTION
        Two separate audiences, both needed:
          - $env:PATH covers the current process, so a script dot-sourcing
            this can call the tool it just installed.
          - $env:GITHUB_PATH covers every *later step* of the same Actions
            job, which each get a fresh process built from the runner
            service's stale environment.
        Neither writes to the machine PATH - the MSI already did that, for
        every process started after the next service restart.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory)

    $normalized = $Directory.TrimEnd('\')
    $entries = @($env:PATH -split ';' | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') })
    if ($entries -notcontains $normalized) {
        $env:PATH = "$normalized;$env:PATH"
    }

    if ($env:GITHUB_PATH) {
        Add-Content -Path $env:GITHUB_PATH -Value $normalized
    }
}

function Install-AerieAwsCli {
    <#
    .SYNOPSIS
        Ensures the AWS CLI v2 is installed at or above the pin in
        scripts\versions.json, and returns the path to aws.exe.

    .PARAMETER CheckOnly
        Report what would be installed and fail rather than touching the
        machine. For a run that legitimately can't install - a by-hand
        invocation as a non-administrator - so the answer is the same message
        either way.

    .NOTES
        Idempotent: an install that's already current does nothing but resolve
        a path, so this is safe to run at the top of every provisioning job.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    $pinnedVersion = Get-AerieVersion -Name 'awsCli.version' -Pattern '^\d+\.\d+\.\d+$'
    $existingPath = Get-AerieAwsCliPath
    $existingVersion = if ($existingPath) { Get-AerieAwsCliVersion -Path $existingPath } else { $null }

    # A floor rather than an exact match - see the awsCli note in
    # scripts\versions.json. These runners are shared with cd.yml and whatever
    # else the operator dispatches, so silently downgrading a newer CLI
    # somebody else's job depends on would be the worse outcome.
    if ($existingVersion -and ([version]$existingVersion -ge [version]$pinnedVersion)) {
        Write-Host "AWS CLI: $existingVersion at $existingPath (pin is $pinnedVersion) - nothing to do."
        Publish-AerieToolPath -Directory (Split-Path $existingPath -Parent)
        return $existingPath
    }

    $reason = if ($existingVersion) {
        "AWS CLI $existingVersion is older than the pinned $pinnedVersion"
    }
    elseif ($existingPath) {
        "'$existingPath' didn't answer to --version like the AWS CLI"
    }
    else {
        'the AWS CLI is not installed'
    }

    if ($CheckOnly) {
        throw "$reason, and -CheckOnly was passed. Run scripts\runner\Install-RunnerDependencies.ps1 -Dependency AwsCli from an elevated shell on this machine, or let the provisioning workflow's 'Ensure runner dependencies' step do it."
    }

    if (-not (Test-AerieAdministrator)) {
        throw "$reason, and installing it needs an elevated session. Re-run this from an elevated PowerShell, or dispatch the workflow - the runner service runs as a local Administrator (see .github/workflows/provision-0-new-node.yml)."
    }

    $url = Get-AerieVersion -Name 'awsCli.url' -Pattern '^https://'
    $sha256 = Get-AerieVersion -Name 'awsCli.sha256' -Pattern '^[0-9a-fA-F]{64}$'

    Write-Host "AWS CLI: $reason - installing $pinnedVersion."

    # Function-scoped, so the caller's preferences are untouched on return.
    # Windows PowerShell 5.1 renders a progress bar per chunk, which costs more
    # than the ~50MB download itself; TLS 1.2 is pinned because 5.1 still
    # negotiates SSL3/TLS1.0 by default on some Server SKUs. Same reasoning as
    # hyperv\Get-GoldenImage.ps1.
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $stamp = [Guid]::NewGuid().ToString('N')
    $msiPath = Join-Path ([IO.Path]::GetTempPath()) "aerie-awscli-$pinnedVersion-$stamp.msi"
    $logPath = Join-Path ([IO.Path]::GetTempPath()) "aerie-awscli-$pinnedVersion-$stamp.log"

    try {
        Write-Host "  Downloading $url ..."
        Invoke-WebRequest -Uri $url -OutFile $msiPath -UseBasicParsing

        $actual = (Get-FileHash -Path $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $sha256.ToLowerInvariant()) {
            throw "SHA256 mismatch for $url.`n  expected: $($sha256.ToLowerInvariant())`n  actual:   $actual`nRefusing to install an installer that doesn't match the pin in scripts\versions.json."
        }
        Write-Host '  OK - MSI matches the pinned SHA256.'

        # /norestart because this runs mid-job on a machine that may be part
        # way through provisioning a node; a reboot is reported (3010) and left
        # to the operator rather than taken.
        $process = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
            '/i', "`"$msiPath`"", '/qn', '/norestart', '/L*V', "`"$logPath`"")

        switch ($process.ExitCode) {
            0 { }
            3010 {
                Write-Warning 'The AWS CLI installed, but Windows reports a reboot is pending. The CLI itself is usable now; the pending reboot is from something else on this machine.'
            }
            1638 {
                # Only reachable if another version landed between the version
                # probe above and this call - a concurrent job, or a vendor
                # updater. Whatever is there now is at least as new as what was
                # probed, so use it rather than fighting over the machine.
                Write-Warning 'msiexec reports another version of the AWS CLI is already installed (1638) - it changed underneath this run. Continuing with whatever is on the machine now.'
            }
            default {
                Get-Content $logPath -Tail 40 -ErrorAction SilentlyContinue | Write-Output
                throw "AWS CLI install failed with msiexec exit code $($process.ExitCode). The last 40 lines of $logPath are above."
            }
        }
    }
    finally {
        Remove-Item $msiPath -Force -ErrorAction SilentlyContinue
        Remove-Item $logPath -Force -ErrorAction SilentlyContinue
    }

    # Re-resolve rather than assuming the install directory: this is also what
    # proves the install actually produced a working binary.
    $installedPath = Get-AerieAwsCliPath
    if (-not $installedPath) {
        throw "msiexec reported success but aws.exe still isn't anywhere this script looks ($((Get-AerieAwsCliSearchDirectory) -join '; ')). Check the machine by hand."
    }

    Publish-AerieToolPath -Directory (Split-Path $installedPath -Parent)

    $installedVersion = Get-AerieAwsCliVersion -Path $installedPath
    if (-not $installedVersion) {
        throw "'$installedPath' exists after the install but doesn't answer to --version. Check the machine by hand."
    }
    if ($installedVersion -ne $pinnedVersion) {
        Write-Warning "Installed the pinned $pinnedVersion MSI but aws.exe reports $installedVersion. Continuing - but check whether a second AWS CLI is shadowing it on PATH."
    }

    Write-Host "AWS CLI: $installedVersion at $installedPath."
    return $installedPath
}

function Install-AerieOpenSshClient {
    <#
    .SYNOPSIS
        Ensures ssh.exe and ssh-keygen.exe are present, installing the
        OpenSSH.Client Windows capability if they aren't.

    .PARAMETER CheckOnly
        Report and fail rather than touching the machine.

    .NOTES
        Version-less on purpose, unlike the AWS CLI: this is a Windows
        optional feature, so the build is the one that ships with the host's
        OS. There is nothing to pin and nothing to download - which is also
        why a pin in scripts\versions.json would be a lie.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    $required = @('ssh.exe', 'ssh-keygen.exe')
    $missing = @($required | Where-Object { -not (Get-Command $_ -CommandType Application -ErrorAction SilentlyContinue) })
    if ($missing.Count -eq 0) {
        $ssh = Get-Command ssh.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        Write-Host "OpenSSH client: present ($($ssh.Source))."
        return
    }

    if ($CheckOnly) {
        throw "$($missing -join ', ') not found, and -CheckOnly was passed. Run scripts\runner\Install-RunnerDependencies.ps1 -Dependency OpenSshClient from an elevated shell on this machine."
    }

    if (-not (Test-AerieAdministrator)) {
        throw "$($missing -join ', ') not found, and adding the OpenSSH.Client Windows capability needs an elevated session. Re-run this from an elevated PowerShell, or dispatch the workflow - the runner service runs as a local Administrator."
    }

    Write-Host "OpenSSH client: $($missing -join ', ') missing - adding the OpenSSH.Client capability."

    # Enumerated rather than hard-coding OpenSSH.Client~~~~0.0.1.0: the version
    # suffix is part of the capability *name* and differs across Windows
    # builds, so a literal that's right on Server 2022 is a
    # CapabilityNotFound on the next SKU.
    $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Client*' -ErrorAction Stop |
        Select-Object -First 1
    if (-not $capability) {
        throw "This Windows build offers no OpenSSH.Client capability. Install Win32-OpenSSH by hand (https://github.com/PowerShell/Win32-OpenSSH/releases) and put ssh.exe on PATH."
    }

    if ($capability.State -ne 'Installed') {
        $result = Add-WindowsCapability -Online -Name $capability.Name
        if ($result -and $result.RestartNeeded) {
            Write-Warning 'Windows reports a restart is needed to finish adding the OpenSSH client. ssh.exe is usually usable immediately; if the check below fails, restart this machine.'
        }
    }

    # The capability installs into System32\OpenSSH, which is on the machine
    # PATH but not on this already-running process's copy of it.
    Publish-AerieToolPath -Directory (Join-Path $env:SystemRoot 'System32\OpenSSH')

    # Checked on disk as well as through Get-Command: PowerShell's command
    # lookup for applications is served from a cache built when the session
    # started, so a binary that appeared during this session can be present
    # and still not be discoverable until something invalidates it.
    $opensshDirectory = Join-Path $env:SystemRoot 'System32\OpenSSH'
    $stillMissing = @($required | Where-Object {
            -not (Get-Command $_ -CommandType Application -ErrorAction SilentlyContinue) -and
            -not (Test-Path (Join-Path $opensshDirectory $_) -PathType Leaf)
        })
    if ($stillMissing.Count -gt 0) {
        throw "Added the OpenSSH.Client capability but $($stillMissing -join ', ') still isn't there. This machine may need a restart to finish the install."
    }

    Write-Host "OpenSSH client: installed into $opensshDirectory."
}
