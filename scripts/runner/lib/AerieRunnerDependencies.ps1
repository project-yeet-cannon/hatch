<#
.SYNOPSIS
    Locates - and, when asked, installs - the third-party tooling an Aerie
    self-hosted runner needs: the AWS CLI v2 and the Windows OpenSSH client
    for the provisioning scripts, plus the build toolchain (pwsh, kubectl,
    helm, jq, gh, the Android SDK) that ci.yml and publish.yml stopped getting
    for free when they moved off GitHub's ubuntu-latest image.

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

# ---------------------------------------------------------------------------
# CI tooling
#
# Everything below exists because ci.yml and publish.yml moved off
# GitHub's ubuntu-latest image onto these runners. That image came with
# pwsh, kubectl, helm, jq, gh, Docker and a full Android SDK preinstalled;
# a Windows runner comes with none of them, and a prerequisite that lives
# only in a README is wrong on the next machine (see this file's header).
#
# Docker is the one deliberate omission: a Linux-container daemon on Windows
# is Docker Desktop or a WSL2/VM arrangement, not a hash-pinned MSI, so it
# stays an operator-installed prerequisite. Get-AerieDockerPath below only
# *checks* for it, and says what is missing when it isn't there.
# ---------------------------------------------------------------------------

function Get-AerieRunnerToolRoot {
    <#
    .SYNOPSIS
        The directory pinned CI tools are unpacked into.

    .DESCRIPTION
        Under ProgramData rather than the runner's work directory: the work
        directory is wiped between jobs, which would re-download ~700MB of
        Android SDK every push. Version-stamped subdirectories mean a bump in
        scripts\versions.json installs alongside the old one rather than
        fighting it, and an unchanged pin is a Test-Path away from being a
        no-op.
    #>
    [CmdletBinding()]
    param()

    $base = $env:ProgramData
    if (-not $base) { $base = Join-Path $env:SystemDrive 'ProgramData' }
    return Join-Path $base 'Aerie\runner-tools'
}

function Publish-AerieToolEnv {
    <#
    .SYNOPSIS
        Sets an environment variable for this process and for every later step
        of the same Actions job.

    .DESCRIPTION
        The $env:GITHUB_PATH counterpart of Publish-AerieToolPath, for the
        tools that are found through a variable rather than through PATH -
        ANDROID_HOME being the one that matters here. Same two audiences, same
        reason: each later step is a fresh process built from the runner
        service's stale environment.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    Set-Item -Path "env:$Name" -Value $Value

    if ($env:GITHUB_ENV) {
        Add-Content -Path $env:GITHUB_ENV -Value "$Name=$Value"
    }
}

function Save-AeriePinnedArtifact {
    <#
    .SYNOPSIS
        Downloads a pinned URL and refuses to return unless it matches the
        pinned SHA256.

    .DESCRIPTION
        Factored out of Install-AerieAwsCli's body so every tool added here
        gets the same verification rather than a hand-rolled near-copy of it.
        The hash check is the whole point of the pin: an unverified URL is
        worse than no pin at all (scripts\versions.json says so).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][string]$OutFile
    )

    # Function-scoped, so the caller's preferences are untouched on return.
    # Windows PowerShell 5.1 renders a progress bar per chunk, which on the
    # ~140MB Android zip costs several times the download itself; TLS 1.2 is
    # pinned because 5.1 still negotiates SSL3/TLS1.0 by default on some
    # Server SKUs.
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    Write-Host "  Downloading $Url ..."
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing

    $actual = (Get-FileHash -Path $OutFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        Remove-Item $OutFile -Force -ErrorAction SilentlyContinue
        throw "SHA256 mismatch for $Url.`n  expected: $($Sha256.ToLowerInvariant())`n  actual:   $actual`nRefusing to use a download that doesn't match the pin in scripts\versions.json."
    }
    Write-Host '  OK - matches the pinned SHA256.'
}

function Get-AerieToolVersion {
    <#
    .SYNOPSIS
        Runs a tool's version command and pulls a dotted version out of it, or
        returns $null.

    .DESCRIPTION
        $null means "something is there but it doesn't answer like the tool we
        expect", which every caller treats as needing an install rather than
        as a hard failure - the same contract Get-AerieAwsCliVersion has.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Pattern
    )

    # Merging stderr into the pipeline raises a terminating NativeCommandError
    # under $ErrorActionPreference = 'Stop' in Windows PowerShell 5.1 - the
    # same reason Get-AerieAwsCliVersion drops to Continue.
    $ErrorActionPreference = 'Continue'
    $output = & $Path @Arguments 2>&1
    if (($output -join ' ') -match $Pattern) { return $Matches[1] }
    return $null
}

function Install-AeriePinnedTool {
    <#
    .SYNOPSIS
        Ensures one pinned single-binary CLI (kubectl, helm, jq, gh) is
        available, and returns the path to it.

    .DESCRIPTION
        One function rather than four near-identical ones. The shape they
        share:

          1. If the tool is already on PATH at or above the pin, use it. These
             runners are shared with cd.yml and whatever else the operator
             dispatches, so downgrading a tool another job depends on is the
             worse failure - the same floor-not-exact-match rule the AWS CLI
             pin documents.
          2. Otherwise, if this exact pinned version is already unpacked under
             the tool root, publish it. This is the common case on every run
             after the first, and it costs a Test-Path.
          3. Otherwise download, verify, unpack.

    .PARAMETER ArchiveEntry
        Path of the .exe inside the .zip. Omit for a URL that is itself the
        .exe, which is how jq and kubectl ship.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ManifestKey,
        [Parameter(Mandatory)][string]$ExeName,
        [Parameter(Mandatory)][string[]]$VersionArguments,
        [Parameter(Mandatory)][string]$VersionPattern,
        [string]$ArchiveEntry,
        [switch]$CheckOnly
    )

    $pinnedVersion = Get-AerieVersion -Name "$ManifestKey.version" -Pattern '^\d+\.\d+\.\d+$'

    $onPath = Get-Command $ExeName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) {
        $found = Get-AerieToolVersion -Path $onPath.Source -Arguments $VersionArguments -Pattern $VersionPattern
        if ($found -and ([version]$found -ge [version]$pinnedVersion)) {
            Write-Host "$Name`: $found at $($onPath.Source) (pin is $pinnedVersion) - nothing to do."
            return $onPath.Source
        }
    }

    $targetDirectory = Join-Path (Get-AerieRunnerToolRoot) "$ManifestKey\$pinnedVersion"
    $targetPath = Join-Path $targetDirectory $ExeName

    if (Test-Path $targetPath -PathType Leaf) {
        Write-Host "$Name`: $pinnedVersion already at $targetPath - nothing to do."
        Publish-AerieToolPath -Directory $targetDirectory
        return $targetPath
    }

    if ($CheckOnly) {
        throw "$Name $pinnedVersion is not installed, and -CheckOnly was passed. Run scripts\runner\Install-RunnerDependencies.ps1 -Dependency $Name from an elevated shell on this machine, or let the workflow's 'Ensure runner dependencies' step do it."
    }

    $url = Get-AerieVersion -Name "$ManifestKey.url" -Pattern '^https://'
    $sha256 = Get-AerieVersion -Name "$ManifestKey.sha256" -Pattern '^[0-9a-fA-F]{64}$'

    Write-Host "$Name`: installing $pinnedVersion into $targetDirectory."

    $stamp = [Guid]::NewGuid().ToString('N')
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "aerie-$ManifestKey-$stamp"
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    try {
        if ($ArchiveEntry) {
            $archivePath = Join-Path $scratch 'download.zip'
            Save-AeriePinnedArtifact -Url $url -Sha256 $sha256 -OutFile $archivePath

            $extracted = Join-Path $scratch 'x'
            Expand-Archive -Path $archivePath -DestinationPath $extracted -Force

            $source = Join-Path $extracted $ArchiveEntry
            if (-not (Test-Path $source -PathType Leaf)) {
                throw "$Name's archive unpacked but '$ArchiveEntry' isn't in it. The upstream layout changed - fix ArchiveEntry for $ManifestKey."
            }

            # Staged into place only after the hash passed and the entry was
            # found, so an interrupted run never leaves a half-populated
            # version directory that step 2 above would later trust.
            New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
            Move-Item -Path $source -Destination $targetPath -Force
        }
        else {
            $staged = Join-Path $scratch $ExeName
            Save-AeriePinnedArtifact -Url $url -Sha256 $sha256 -OutFile $staged

            New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
            Move-Item -Path $staged -Destination $targetPath -Force
        }
    }
    finally {
        Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }

    Publish-AerieToolPath -Directory $targetDirectory

    # Re-resolve rather than trusting the copy - this is also what proves the
    # download is a working binary for this architecture.
    $installedVersion = Get-AerieToolVersion -Path $targetPath -Arguments $VersionArguments -Pattern $VersionPattern
    if (-not $installedVersion) {
        throw "'$targetPath' exists after the install but doesn't answer to '$($VersionArguments -join ' ')'. Check the machine by hand."
    }

    Write-Host "$Name`: $installedVersion at $targetPath."
    return $targetPath
}

function Install-AerieKubectl {
    <#
    .SYNOPSIS
        Ensures kubectl (which carries kustomize) is available. Used by
        ci.yml's deploy-manifests job.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    return Install-AeriePinnedTool -Name 'Kubectl' -ManifestKey 'kubectl' `
        -ExeName 'kubectl.exe' `
        -VersionArguments @('version', '--client') `
        -VersionPattern 'v(\d+\.\d+\.\d+)' `
        -CheckOnly:$CheckOnly
}

function Install-AerieHelm {
    <#
    .SYNOPSIS
        Ensures helm is available. Used by ci.yml to lint and render
        charts/aerie.

    .NOTES
        The Windows archive nests the binary under windows-amd64\.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    return Install-AeriePinnedTool -Name 'Helm' -ManifestKey 'helm' `
        -ExeName 'helm.exe' -ArchiveEntry 'windows-amd64\helm.exe' `
        -VersionArguments @('version', '--short') `
        -VersionPattern 'v(\d+\.\d+\.\d+)' `
        -CheckOnly:$CheckOnly
}

function Install-AerieJq {
    <#
    .SYNOPSIS
        Ensures jq is available. Used by ci.yml and by both composite actions
        under .github/actions/.

    .NOTES
        `jq --version` prints "jq-1.8.1", hence the dash in the pattern.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    return Install-AeriePinnedTool -Name 'Jq' -ManifestKey 'jq' `
        -ExeName 'jq.exe' `
        -VersionArguments @('--version') `
        -VersionPattern 'jq-(\d+\.\d+\.\d+)' `
        -CheckOnly:$CheckOnly
}

function Install-AerieGitHubCli {
    <#
    .SYNOPSIS
        Ensures gh is available. .github/actions/wait-for-ci calls it, which
        is the gate every publish.yml image push waits on.

    .NOTES
        The Windows archive nests the binary under bin\.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    return Install-AeriePinnedTool -Name 'GitHubCli' -ManifestKey 'githubCli' `
        -ExeName 'gh.exe' -ArchiveEntry 'bin\gh.exe' `
        -VersionArguments @('--version') `
        -VersionPattern 'gh version (\d+\.\d+\.\d+)' `
        -CheckOnly:$CheckOnly
}

function Get-AeriePowerShell7Path {
    <#
    .SYNOPSIS
        Returns the full path to pwsh.exe, or $null if PowerShell 7 isn't
        installed.

    .DESCRIPTION
        PATH first, then the MSI's install directory - the same two-lookup
        reason Get-AerieAwsCliPath documents: the installer writes the machine
        PATH, which the already-running runner service cannot see until it
        restarts.
    #>
    [CmdletBinding()]
    param()

    $onPath = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    $roots = @($env:ProgramW6432, $env:ProgramFiles, ${env:ProgramFiles(x86)}) |
        Where-Object { $_ } |
        Select-Object -Unique

    foreach ($root in $roots) {
        $candidate = Join-Path $root 'PowerShell\7\pwsh.exe'
        if (Test-Path $candidate -PathType Leaf) { return $candidate }
    }

    return $null
}

function Install-AeriePowerShell7 {
    <#
    .SYNOPSIS
        Ensures pwsh (PowerShell 7) is installed at or above the pin, and
        returns the path to it.

    .DESCRIPTION
        ci.yml's deploy-manifests job runs New-ExternalSecrets.ps1 through
        `pwsh`. On GitHub's Ubuntu image that interpreter was free; here it
        has to be installed. Note this does not replace Windows PowerShell
        5.1 - it installs beside it, which is why the provisioning scripts'
        5.1 compatibility (see this file's .NOTES) still matters and is
        unaffected.

    .PARAMETER CheckOnly
        Report and fail rather than touching the machine.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    $pinnedVersion = Get-AerieVersion -Name 'powerShell.version' -Pattern '^\d+\.\d+\.\d+$'
    $existingPath = Get-AeriePowerShell7Path
    $existingVersion = $null
    if ($existingPath) {
        $existingVersion = Get-AerieToolVersion -Path $existingPath `
            -Arguments @('-NoProfile', '-NoLogo', '-Command', '$PSVersionTable.PSVersion.ToString()') `
            -Pattern '^(\d+\.\d+\.\d+)'
    }

    # A floor, not an exact match - same shared-runner reasoning as the AWS
    # CLI: downgrading a pwsh another job depends on is the worse failure.
    if ($existingVersion -and ([version]$existingVersion -ge [version]$pinnedVersion)) {
        Write-Host "PowerShell 7: $existingVersion at $existingPath (pin is $pinnedVersion) - nothing to do."
        Publish-AerieToolPath -Directory (Split-Path $existingPath -Parent)
        return $existingPath
    }

    $reason = if ($existingVersion) {
        "pwsh $existingVersion is older than the pinned $pinnedVersion"
    }
    elseif ($existingPath) {
        "'$existingPath' didn't answer with a version like PowerShell"
    }
    else {
        'PowerShell 7 is not installed'
    }

    if ($CheckOnly) {
        throw "$reason, and -CheckOnly was passed. Run scripts\runner\Install-RunnerDependencies.ps1 -Dependency PowerShell7 from an elevated shell on this machine."
    }

    if (-not (Test-AerieAdministrator)) {
        throw "$reason, and installing it needs an elevated session. Re-run this from an elevated PowerShell, or dispatch the workflow - the runner service runs as a local Administrator."
    }

    $url = Get-AerieVersion -Name 'powerShell.url' -Pattern '^https://'
    $sha256 = Get-AerieVersion -Name 'powerShell.sha256' -Pattern '^[0-9a-fA-F]{64}$'

    Write-Host "PowerShell 7: $reason - installing $pinnedVersion."

    $stamp = [Guid]::NewGuid().ToString('N')
    $msiPath = Join-Path ([IO.Path]::GetTempPath()) "aerie-pwsh-$pinnedVersion-$stamp.msi"
    $logPath = Join-Path ([IO.Path]::GetTempPath()) "aerie-pwsh-$pinnedVersion-$stamp.log"

    try {
        Save-AeriePinnedArtifact -Url $url -Sha256 $sha256 -OutFile $msiPath

        # /norestart for the same reason the AWS CLI install takes it: this can
        # run mid-job on a machine part way through provisioning a node, so a
        # pending reboot is reported and left to the operator rather than
        # taken. The ADD_PATH property is what puts pwsh on the machine PATH.
        $process = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
            '/i', "`"$msiPath`"", '/qn', '/norestart', 'ADD_PATH=1', '/L*V', "`"$logPath`"")

        switch ($process.ExitCode) {
            0 { }
            3010 {
                Write-Warning 'PowerShell 7 installed, but Windows reports a reboot is pending. pwsh is usable now; the pending reboot is from something else on this machine.'
            }
            1638 {
                Write-Warning 'msiexec reports another version of PowerShell 7 is already installed (1638) - it changed underneath this run. Continuing with whatever is on the machine now.'
            }
            default {
                Get-Content $logPath -Tail 40 -ErrorAction SilentlyContinue | Write-Output
                throw "PowerShell 7 install failed with msiexec exit code $($process.ExitCode). The last 40 lines of $logPath are above."
            }
        }
    }
    finally {
        Remove-Item $msiPath -Force -ErrorAction SilentlyContinue
        Remove-Item $logPath -Force -ErrorAction SilentlyContinue
    }

    $installedPath = Get-AeriePowerShell7Path
    if (-not $installedPath) {
        throw "msiexec reported success but pwsh.exe still isn't anywhere this script looks. Check the machine by hand."
    }

    Publish-AerieToolPath -Directory (Split-Path $installedPath -Parent)

    Write-Host "PowerShell 7: installed at $installedPath."
    return $installedPath
}

function Get-AerieDockerPath {
    <#
    .SYNOPSIS
        Returns the full path to docker.exe, or $null.

    .NOTES
        Resolve-only, deliberately - there is no Install-AerieDocker below and
        that is not an oversight. Building the repository's Linux images on a
        Windows host means Docker Desktop with a WSL2 backend, or a daemon in
        a VM; neither is a hash-pinned MSI that can be dropped on a machine
        unattended, and a half-working silent install of the thing every image
        build depends on is worse than a clear message saying it's missing.
        So Docker stays the one operator-installed prerequisite, and this
        reports on it.
    #>
    [CmdletBinding()]
    param()

    $onPath = Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    return $null
}

function Install-AerieDocker {
    <#
    .SYNOPSIS
        Checks that a Docker daemon capable of building this repository's
        Linux images is reachable. Never installs - see Get-AerieDockerPath.

    .DESCRIPTION
        Three separate things can be wrong, and they have different fixes, so
        they are reported separately rather than as one "docker failed":

          - docker.exe missing entirely
          - docker.exe present but the daemon isn't answering (Docker Desktop
            not started - it is not a service, so a runner rebooting without
            anyone logging in lands exactly here)
          - the daemon answering but in Windows-container mode, which cannot
            build any Dockerfile in this repository

        The third is the quiet one: everything looks installed and every build
        fails with an unhelpful base-image error.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    $dockerPath = Get-AerieDockerPath
    if (-not $dockerPath) {
        throw "docker.exe not found. Every publish.yml job builds an image, so this runner needs a Docker daemon that can build Linux containers - Docker Desktop with the WSL2 backend is what the other runners use. This is the one dependency Install-RunnerDependencies.ps1 will not install for you (see Get-AerieDockerPath in scripts\runner\lib\AerieRunnerDependencies.ps1)."
    }

    # Merging stderr, and tolerating a non-zero exit, for the same reason
    # Get-AerieAwsCliVersion does: a daemon that isn't running makes this a
    # NativeCommandError rather than a readable message.
    $ErrorActionPreference = 'Continue'
    $osType = & $dockerPath info --format '{{.OSType}}' 2>&1
    $exit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'

    if ($exit -ne 0) {
        throw "'$dockerPath' is installed but the daemon did not answer ``docker info``:`n$($osType -join "`n")`nIf this is Docker Desktop, it is not a Windows service - it has to be started, and it does not start on its own after a reboot with nobody logged in. Enable 'Start Docker Desktop when you sign in' plus an auto-signed-in session, or run the daemon as a service."
    }

    $osType = ($osType -join '').Trim()
    if ($osType -ne 'linux') {
        throw "The Docker daemon on this runner is in '$osType'-container mode. Every Dockerfile in this repository is a Linux image, so builds here would fail on the base image. Switch the daemon to Linux containers (Docker Desktop: 'Switch to Linux containers')."
    }

    Write-Host "Docker: $dockerPath, daemon in linux-container mode."
}

function Install-AerieAndroidSdk {
    <#
    .SYNOPSIS
        Ensures an Android SDK matching apps/kiosk's compileSdk is present,
        and publishes ANDROID_HOME for the rest of the job.

    .DESCRIPTION
        Two stages, because that is how Google ships it: a hash-pinned
        command-line-tools zip containing sdkmanager, and then whatever
        sdkmanager is told to fetch. Only the first stage can be pinned by
        hash - the packages sdkmanager downloads are verified by sdkmanager
        against its own repository manifest, which is the only integrity story
        Google offers for them.

        Installed under ProgramData rather than the job workspace: it is about
        700MB once the platform and build-tools land, and the workspace is
        wiped between jobs.

    .PARAMETER CheckOnly
        Report and fail rather than touching the machine.

    .NOTES
        Needs a JDK on PATH - sdkmanager is a Java program. In a workflow that
        means this runs *after* actions/setup-java, which is the ordering
        ci.yml and publish.yml use.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    $cmdlineVersion = Get-AerieVersion -Name 'androidSdk.cmdlineToolsVersion' -Pattern '^\d+$'
    $platform = Get-AerieVersion -Name 'androidSdk.platform' -Pattern '^android-\d+$'
    $buildTools = Get-AerieVersion -Name 'androidSdk.buildTools' -Pattern '^\d+\.\d+\.\d+$'

    $sdkRoot = Join-Path (Get-AerieRunnerToolRoot) 'android-sdk'
    $sdkManager = Join-Path $sdkRoot 'cmdline-tools\latest\bin\sdkmanager.bat'

    # What "installed" means here is the two packages the build actually
    # resolves, not merely the presence of sdkmanager - a run interrupted
    # between the two stages would otherwise look complete forever.
    $platformPath = Join-Path $sdkRoot "platforms\$platform"
    $buildToolsPath = Join-Path $sdkRoot "build-tools\$buildTools"
    $complete = (Test-Path $sdkManager -PathType Leaf) -and
                (Test-Path $platformPath -PathType Container) -and
                (Test-Path $buildToolsPath -PathType Container)

    if ($complete) {
        Write-Host "Android SDK: $platform + build-tools $buildTools already at $sdkRoot - nothing to do."
        Publish-AerieToolEnv -Name 'ANDROID_HOME' -Value $sdkRoot
        Publish-AerieToolEnv -Name 'ANDROID_SDK_ROOT' -Value $sdkRoot
        Publish-AerieToolPath -Directory (Join-Path $sdkRoot 'platform-tools')
        return $sdkRoot
    }

    if ($CheckOnly) {
        throw "The Android SDK at $sdkRoot is missing or incomplete (wanted $platform and build-tools $buildTools), and -CheckOnly was passed. Run scripts\runner\Install-RunnerDependencies.ps1 -Dependency AndroidSdk from an elevated shell on this machine."
    }

    # Checked before the ~140MB download rather than after: sdkmanager is a
    # Java program, and its failure without a JDK is a stack trace rather than
    # a sentence.
    if (-not (Get-Command java -CommandType Application -ErrorAction SilentlyContinue)) {
        throw "No java on PATH, and sdkmanager needs a JDK to run. In a workflow, put actions/setup-java *before* the step that ensures this dependency - see ci.yml's kiosk job."
    }

    if (-not (Test-Path $sdkManager -PathType Leaf)) {
        $url = Get-AerieVersion -Name 'androidSdk.url' -Pattern '^https://'
        $sha256 = Get-AerieVersion -Name 'androidSdk.sha256' -Pattern '^[0-9a-fA-F]{64}$'

        Write-Host "Android SDK: installing command-line tools $cmdlineVersion into $sdkRoot."

        $stamp = [Guid]::NewGuid().ToString('N')
        $scratch = Join-Path ([IO.Path]::GetTempPath()) "aerie-androidsdk-$stamp"
        New-Item -ItemType Directory -Path $scratch -Force | Out-Null

        try {
            $zipPath = Join-Path $scratch 'cmdline-tools.zip'
            Save-AeriePinnedArtifact -Url $url -Sha256 $sha256 -OutFile $zipPath

            $extracted = Join-Path $scratch 'x'
            Expand-Archive -Path $zipPath -DestinationPath $extracted -Force

            # The zip unpacks to a top-level 'cmdline-tools' directory, but
            # sdkmanager insists on living at cmdline-tools\<channel>\ inside
            # the SDK root and resolves its own location relative to that - so
            # unpacking it straight into the root gives a sdkmanager.bat that
            # fails with "Could not determine SDK root". 'latest' is the
            # channel name the Android tooling expects.
            $unpacked = Join-Path $extracted 'cmdline-tools'
            if (-not (Test-Path $unpacked -PathType Container)) {
                throw "The command-line-tools zip didn't unpack to a 'cmdline-tools' directory. The upstream layout changed - check androidSdk.url in scripts\versions.json."
            }

            $destination = Join-Path $sdkRoot 'cmdline-tools'
            New-Item -ItemType Directory -Path $destination -Force | Out-Null

            $latest = Join-Path $destination 'latest'
            if (Test-Path $latest) { Remove-Item $latest -Recurse -Force }
            Move-Item -Path $unpacked -Destination $latest -Force
        }
        finally {
            Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }

        if (-not (Test-Path $sdkManager -PathType Leaf)) {
            throw "Unpacked the command-line tools but $sdkManager still isn't there. Check the machine by hand."
        }
    }

    # Licences first and separately. sdkmanager will not install a package
    # whose licence is unaccepted, and prompts for it on stdin - which in a
    # non-interactive job is a hang, not an error. The 'y' stream is how the
    # tool is meant to be driven unattended; there is no --accept flag.
    Write-Host 'Android SDK: accepting licences.'
    $accept = ((1..50 | ForEach-Object { 'y' }) -join [Environment]::NewLine)
    $accept | & $sdkManager "--sdk_root=$sdkRoot" --licenses | Out-Null

    $packages = @('platform-tools', "platforms;$platform", "build-tools;$buildTools")
    Write-Host "Android SDK: installing $($packages -join ', ')."
    & $sdkManager "--sdk_root=$sdkRoot" @packages
    if ($LASTEXITCODE -ne 0) {
        throw "sdkmanager exited $LASTEXITCODE installing $($packages -join ', '). Its output is above."
    }

    $missing = @()
    if (-not (Test-Path $platformPath -PathType Container)) { $missing += $platform }
    if (-not (Test-Path $buildToolsPath -PathType Container)) { $missing += "build-tools $buildTools" }
    if ($missing.Count -gt 0) {
        throw "sdkmanager reported success but $($missing -join ' and ') isn't under $sdkRoot. Check that androidSdk.platform and androidSdk.buildTools in scripts\versions.json name packages that actually exist."
    }

    Publish-AerieToolEnv -Name 'ANDROID_HOME' -Value $sdkRoot
    Publish-AerieToolEnv -Name 'ANDROID_SDK_ROOT' -Value $sdkRoot
    Publish-AerieToolPath -Directory (Join-Path $sdkRoot 'platform-tools')

    Write-Host "Android SDK: $platform + build-tools $buildTools at $sdkRoot."
    return $sdkRoot
}

function Get-AerieGitBashPath {
    <#
    .SYNOPSIS
        Returns the full path to Git for Windows' bash.exe, or $null.

    .DESCRIPTION
        Deliberately does *not* use `Get-Command bash` - that is precisely the
        thing that is broken. On a Windows host with WSL enabled,
        C:\Windows\System32\bash.exe is the WSL launcher, and System32 almost
        always precedes Git's directory on the machine PATH. So `bash` resolves
        to WSL, and a runner service running as LOCAL SYSTEM gets

            Running WSL as local system is not supported.
            Error code: Bash/WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED

        on the first `shell: bash` step, which reads like a workflow bug and
        isn't one. Git Bash is therefore located by where Git actually is,
        never by asking PATH for 'bash'.

        Three lookups, cheapest first: beside git.exe (which covers a portable
        or non-default install, and is the one that matters because the runner
        already needs git), then the default install directories, then the
        registry key the installer writes.
    #>
    [CmdletBinding()]
    param()

    $candidates = New-Object Collections.Generic.List[string]

    # git.exe normally lives in <root>\cmd\git.exe or <root>\bin\git.exe;
    # bash.exe is in <root>\bin\bash.exe either way.
    $git = Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($git) {
        $root = Split-Path (Split-Path $git.Source -Parent) -Parent
        $candidates.Add((Join-Path $root 'bin\bash.exe'))
    }

    $roots = @($env:ProgramW6432, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA) |
        Where-Object { $_ } |
        Select-Object -Unique
    foreach ($root in $roots) {
        # Both spellings: a machine-wide install is <root>\Git, a per-user one
        # (which is what a non-admin 'winget install Git.Git' leaves behind) is
        # %LOCALAPPDATA%\Programs\Git.
        $candidates.Add((Join-Path $root 'Git\bin\bash.exe'))
        $candidates.Add((Join-Path $root 'Programs\Git\bin\bash.exe'))
    }

    # Get-Item plus GetValue(), never (Get-ItemProperty ...).InstallPath. The
    # entry point sets Set-StrictMode -Version Latest, under which dereferencing
    # a property that isn't there is a *terminating error* rather than an empty
    # value - and that includes dereferencing the $null a missing key returns,
    # so -ErrorAction SilentlyContinue does not save it. lib\AerieVersions.ps1
    # avoids the same trap for the same reason, and its header says so.
    #
    # Getting this wrong is worse than it looks: this loop runs while the
    # candidate list is still being *built*, so a throw here happens before a
    # single path has been tested - including the git.exe-adjacent one above
    # that would have matched.
    foreach ($key in @('HKLM:\SOFTWARE\GitForWindows', 'HKLM:\SOFTWARE\WOW6432Node\GitForWindows')) {
        $item = Get-Item -Path $key -ErrorAction SilentlyContinue
        if (-not $item) { continue }

        $installPath = $item.GetValue('InstallPath')
        if ($installPath) { $candidates.Add((Join-Path $installPath 'bin\bash.exe')) }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate -PathType Leaf) { return $candidate }
    }

    # Recorded so the caller's error can say where it looked - a bare "not
    # found" on a machine that plainly has Git is not an actionable message.
    $script:AerieGitBashSearched = $candidates
    return $null
}

function Install-AerieGitBash {
    <#
    .SYNOPSIS
        Puts Git for Windows' bash.exe ahead of WSL's on PATH for the rest of
        the job. Never installs anything.

    .DESCRIPTION
        Every `run:` block in ci.yml and publish.yml is bash, and those
        workflows set a workflow-level `defaults.run.shell: bash`. The runner
        resolves that shell from PATH per step, which means an entry added to
        $env:GITHUB_PATH here decides which bash every *later* step in the job
        gets - GITHUB_PATH prepends, so this wins over System32.

        Which is the whole fix: nothing is downloaded, the machine PATH is left
        alone (reordering System32 for every process on a Hyper-V host, to suit
        one runner service, is not a trade worth making), and the effect is
        scoped to the jobs that ask for it.

        This has to be the first step after checkout in any job that runs bash,
        because it cannot fix a step that already ran.

    .NOTES
        No -CheckOnly branch that behaves differently: this never mutates the
        machine, so the check and the fix are the same operation.
    #>
    [CmdletBinding()]
    param([switch]$CheckOnly)

    $script:AerieGitBashSearched = @()
    $bash = Get-AerieGitBashPath
    if (-not $bash) {
        $looked = ($script:AerieGitBashSearched | ForEach-Object { "  - $_" }) -join "`n"
        throw "Git for Windows' bash.exe not found. Every 'shell: bash' step needs it, and on this host 'bash' otherwise resolves to WSL (C:\Windows\System32\bash.exe), which cannot run under the runner service's LOCAL SYSTEM account. Install Git for Windows - https://git-scm.com/download/win - which the runner needs for checkout anyway.`n`nLooked in:`n$looked"
    }

    # Proves it is really Git Bash and not something else called bash.exe -
    # and, more usefully, that it runs at all under this service account.
    $version = Get-AerieToolVersion -Path $bash -Arguments @('--version') -Pattern 'version (\d+\.\d+\.\d+)'
    if (-not $version) {
        throw "'$bash' exists but didn't answer to --version like GNU bash. Check the machine by hand."
    }

    Publish-AerieToolPath -Directory (Split-Path $bash -Parent)

    Write-Host "Git Bash: $version at $bash (now ahead of any WSL bash for the rest of this job)."
    return $bash
}
