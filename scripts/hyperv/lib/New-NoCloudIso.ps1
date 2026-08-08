<#
.SYNOPSIS
    Builds a cloud-init NoCloud seed ISO from a folder containing
    user-data / meta-data, using Windows' built-in IMAPI2FS COM component —
    no Windows ADK / oscdimg.exe / third-party tooling required.

.NOTES
    The volume label must be exactly "cidata" (case-insensitive) — that's
    what cloud-init's NoCloud datasource scans attached media for.
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
    $fsi.FileSystemsToCreate = 1   # FsiFileSystemISO9660 only — NoCloud doesn't need Joliet/UDF
    $fsi.VolumeName = 'cidata'

    # $false = drop the files directly at ISO root instead of nesting them
    # under a "cidata\" directory.
    $fsi.Root.AddTree($SourceFolder, $false)

    $result = $fsi.CreateResultImage()

    if (Test-Path $IsoPath) { Remove-Item $IsoPath -Force }
    [Aerie.IsoWriter]::Write($IsoPath, $result.ImageStream, $result.BlockSize, $result.TotalBlocks)

    Write-Verbose "Wrote NoCloud seed ISO: $IsoPath"
}
