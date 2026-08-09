<#
.SYNOPSIS
    Builds a cloud-init NoCloud seed ISO from a folder containing
    user-data / meta-data, using Windows' built-in IMAPI2FS COM component —
    no Windows ADK / oscdimg.exe / third-party tooling required.

.NOTES
    The volume label must be exactly "cidata" (case-insensitive) — that's
    what cloud-init's NoCloud datasource scans attached media for.

    The ISO is mounted back and checked before this returns; see
    Assert-NoCloudIso for why that isn't paranoia.
#>

function New-NoCloudIso {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateScript({ Test-Path $_ -PathType Container })]
        [string]$SourceFolder,

        [Parameter(Mandatory)]
        [string]$IsoPath
    )

    # ImageStream (below) is a raw vtable-only IStream, not an IDispatch
    # object, so PowerShell can't call it late-bound. PowerShell's own
    # `[System.Runtime.InteropServices.ComTypes.IStream]$x = $comObject`
    # cast for this interface is unreliable across hosts (throws
    # InvalidCastException on some, silently works on others), and
    # ADODB.Stream.Write() doesn't accept a raw IStream as an argument
    # either. Doing the QueryInterface cast inside compiled C# — where
    # `(IStream)imageStream` is a native CLR COM interop cast — is the
    # well-tested way around both.
    if (-not ('Aerie.IsoWriter' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Aerie {
    public static class IsoWriter {
        public static void Write(string path, object imageStream, int blockSize, int totalBlocks) {
            var stream = (IStream)imageStream;
            byte[] buffer = new byte[blockSize];
            IntPtr bytesReadPtr = Marshal.AllocHGlobal(sizeof(int));
            try {
                using (var file = File.Create(path)) {
                    while (totalBlocks-- > 0) {
                        stream.Read(buffer, blockSize, bytesReadPtr);
                        int bytesRead = Marshal.ReadInt32(bytesReadPtr);
                        if (bytesRead <= 0) { break; }
                        file.Write(buffer, 0, bytesRead);
                    }
                }
            } finally {
                Marshal.FreeHGlobal(bytesReadPtr);
            }
        }
    }
}
'@
    }

    $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    # FsiFileSystemISO9660 (1) | FsiFileSystemJoliet (2). ISO9660 on its own
    # is NOT enough, however little NoCloud appears to need: its filenames are
    # capped at 8.3 and restricted to the d-characters A-Z 0-9 _, so IMAPI
    # silently rewrites 'user-data' as 'USERDA~1'. cloud-init then mounts the
    # cidata volume, fails to find the literal names it wants, and falls back
    # to DataSourceNone - applying nothing, while the VM boots normally and
    # takes its hostname from DHCP, so it looks provisioned right up until
    # every SSH attempt is refused for an account that was never created.
    # Joliet preserves case and hyphens, and Linux's iso9660 driver uses the
    # Joliet tree when (as here) there's no Rock Ridge.
    $fsi.FileSystemsToCreate = 3
    $fsi.VolumeName = 'cidata'

    # $false = drop the files directly at ISO root instead of nesting them
    # under a "cidata\" directory.
    $fsi.Root.AddTree($SourceFolder, $false)

    $result = $fsi.CreateResultImage()

    if (Test-Path $IsoPath) { Remove-Item $IsoPath -Force }
    [Aerie.IsoWriter]::Write($IsoPath, $result.ImageStream, $result.BlockSize, $result.TotalBlocks)

    Write-Verbose "Wrote NoCloud seed ISO: $IsoPath"

    Assert-NoCloudIso -IsoPath $IsoPath -SourceFolder $SourceFolder
}

function Assert-NoCloudIso {
    <#
    .SYNOPSIS
        Mounts a seed ISO that was just written and proves cloud-init's
        NoCloud datasource will actually find what's in it.

    .DESCRIPTION
        A seed ISO whose contents are correct but whose *filenames* aren't is
        indistinguishable, from the outside, from a working one: the VM boots,
        gets its DHCP-reserved address, answers on port 22, and even picks up
        the right hostname from DHCP option 12. What it doesn't have is the
        user cloud-init was supposed to create - so sshd rejects every key
        with a generic "Permission denied (publickey)", which reads as a key
        problem and sends you off rotating secrets for hours.

        That is not hypothetical: it is exactly what an ISO9660-only image did
        here (see FileSystemsToCreate in New-NoCloudIso). The failure is
        silent at every layer that could have caught it, so it gets caught
        here instead, two seconds after the ISO is written and long before a
        VM is built from it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$IsoPath,
        [Parameter(Mandatory)][string]$SourceFolder
    )

    $required = @('user-data', 'meta-data')
    $image = $null
    try {
        $image = Mount-DiskImage -ImagePath $IsoPath -PassThru -ErrorAction Stop

        # Get-Volume can come back empty for a moment after Mount-DiskImage
        # returns - the volume arrival is asynchronous.
        $volume = $null
        foreach ($attempt in 1..20) {
            $volume = $image | Get-Volume -ErrorAction SilentlyContinue
            if ($volume -and $volume.DriveLetter) { break }
            Start-Sleep -Milliseconds 250
        }
        if (-not ($volume -and $volume.DriveLetter)) {
            throw "Wrote $IsoPath, but Windows never surfaced a volume for it, so its contents can't be verified."
        }

        if ($volume.FileSystemLabel -ne 'cidata') {
            throw "Seed ISO $IsoPath has volume label '$($volume.FileSystemLabel)', not 'cidata'. cloud-init's NoCloud datasource only scans media labelled cidata, so this seed would be ignored."
        }

        $root = "$($volume.DriveLetter):\"
        $actual = @(Get-ChildItem -Path $root -Force | Select-Object -ExpandProperty Name)
        $missing = @($required | Where-Object { $actual -notcontains $_ })
        if ($missing) {
            throw @"
Seed ISO $IsoPath is missing $($missing -join ' and ') at its root. cloud-init's NoCloud datasource looks for those exact names; anything else and it falls back to DataSourceNone, applying none of the user-data - no user account, no authorized_keys, no packages - while still booting normally.

  expected: $($required -join ', ')
  found:    $($actual -join ', ')

Names like USERDA~1 mean the image was written as plain ISO9660, whose filenames are capped at 8.3 and can't contain a hyphen. Joliet is what preserves them - see FileSystemsToCreate in New-NoCloudIso.
"@
        }

        # Names being right doesn't prove the bytes are: an encoding or
        # line-ending round-trip between reading the template and writing the
        # seed would leave the filenames untouched and still hand cloud-init a
        # document it refuses to parse.
        foreach ($name in $required) {
            $written = [IO.File]::ReadAllBytes((Join-Path $root $name))
            $source = [IO.File]::ReadAllBytes((Join-Path $SourceFolder $name))
            if ([Convert]::ToBase64String($written) -ne [Convert]::ToBase64String($source)) {
                throw "Seed ISO $IsoPath holds a '$name' that differs from the one written to $SourceFolder ($($written.Length) bytes on the ISO vs $($source.Length) at source). The ISO writer mangled it in transit."
            }
        }

        Write-Verbose "Seed ISO verified: label 'cidata', $($required -join ' + ') present and byte-identical to source."
    }
    finally {
        if ($image) { Dismount-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue | Out-Null }
    }
}
