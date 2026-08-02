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

    $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $fsi.FileSystemsToCreate = 1   # FsiFileSystemISO9660 only — NoCloud doesn't need Joliet/UDF
    $fsi.VolumeName = 'cidata'

    # $false = drop the files directly at ISO root instead of nesting them
    # under a "cidata\" directory.
    $fsi.Root.AddTree($SourceFolder, $false)

    $result = $fsi.CreateResultImage()
    [System.Runtime.InteropServices.ComTypes.IStream]$comStream = $result.ImageStream

    if (Test-Path $IsoPath) { Remove-Item $IsoPath -Force }
    $fileStream = [System.IO.File]::Create($IsoPath)
    try {
        $buffer = New-Object byte[] 65536
        $bytesReadPtr = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(4)
        try {
            do {
                $comStream.Read($buffer, $buffer.Length, $bytesReadPtr)
                $bytesRead = [System.Runtime.InteropServices.Marshal]::ReadInt32($bytesReadPtr)
                if ($bytesRead -gt 0) { $fileStream.Write($buffer, 0, $bytesRead) }
            } while ($bytesRead -eq $buffer.Length)
        } finally {
            [System.Runtime.InteropServices.Marshal]::FreeHGlobal($bytesReadPtr)
        }
    } finally {
        $fileStream.Close()
    }

    Write-Verbose "Wrote NoCloud seed ISO: $IsoPath"
}
