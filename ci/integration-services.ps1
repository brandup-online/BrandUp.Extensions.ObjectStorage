<#
.SYNOPSIS
    Starts or stops the S3 server the integration tests run against, as a native Windows process.

.DESCRIPTION
    The agents of the Test pool run Windows without Docker, and the open-source MinIO is archived with its
    binaries no longer served, so the tests run against the S3 gateway of SeaweedFS, a single native binary.
    Downloads are pinned by version and SHA-256 and cached in the tools directory, which a pipeline agent keeps
    between runs.

    Written for Windows PowerShell 5.1, which every Windows agent has.

.EXAMPLE
    ci/integration-services.ps1 -Action Start
    $env:MINIO_SERVICE_URL = 'http://127.0.0.1:8333'; $env:MINIO_ACCESS_KEY = 'test'; $env:MINIO_SECRET_KEY = 'test'
    $env:S3_EMULATOR = 'seaweedfs'
    dotnet test -c Release --filter-trait Category=Integration
    ci/integration-services.ps1 -Action Stop
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Start', 'Stop')]
    [string] $Action,

    # Downloads are cached here; in a pipeline pass $(Agent.ToolsDirectory).
    [string] $ToolsDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BrandUp\ci-tools'),

    # Process ids, logs and data of the running services; in a pipeline pass a folder under $(Agent.TempDirectory).
    [string] $StateDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'BrandUp.Extensions.ObjectStorage.services')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Tools = @{
    SeaweedFS = @{
        Name    = 'seaweedfs'
        Version = '4.47'
        Url     = 'https://github.com/seaweedfs/seaweedfs/releases/download/4.47/windows_amd64.zip'
        Sha256  = '8809359079E62FCD60574FF661449160899622C52072F3F569D346669079EFE9'
    }
}

# The tests sign with this pair, as they would against a real S3.
$AccessKey = 'test'
$SecretKey = 'test'

# Every port of the all-in-one server; gRPC ports are the HTTP ones plus 10000. The volume server is moved
# off its default 8080, which an agent machine is likely to use for something else.
$S3Port = 8333
$MasterPort = 9333
$FilerPort = 8888
$VolumePort = 8380

. (Join-Path $PSScriptRoot 'service-helpers.ps1')

if ($Action -eq 'Stop') {
    Stop-ServiceProcesses
    return
}

Stop-ServiceProcesses
# Exists however early the start fails, so the pipeline always has a folder of logs to publish.
New-Item $LogDirectory -ItemType Directory -Force | Out-Null
# Before the folders are cleared, so a service left by a run whose process list is lost no longer holds their files.
$S3Port, $MasterPort, $FilerPort, $VolumePort | ForEach-Object { Assert-PortFree $_; Assert-PortFree ($_ + 10000) }

New-ServiceDirectory 'logs' | Out-Null
Start-Transcript (Join-Path $LogDirectory 'start.log') | Out-Null
try {
    $weed = Get-ToolFile (Install-Tool $Tools.SeaweedFS) 'weed.exe'

    $data = New-ServiceDirectory 'seaweedfs-data'
    $s3Config = Join-Path $StateDirectory 'seaweedfs-s3.json'
    @{
        identities = @(@{
                name        = 'ci'
                credentials = @(@{ accessKey = $AccessKey; secretKey = $SecretKey })
                actions     = @('Admin', 'Read', 'List', 'Tagging', 'Write')
            })
    } | ConvertTo-Json -Depth 5 | Set-Content $s3Config -Encoding ASCII

    Start-ServiceProcess 'seaweedfs' $weed @(
        'server', "-dir=$data", '-ip=127.0.0.1',
        "-master.port=$MasterPort", "-filer.port=$FilerPort", "-volume.port=$VolumePort",
        '-master.volumeSizeLimitMB=64', '-volume.max=20',
        '-s3', "-s3.port=$S3Port", "-s3.config=$s3Config", '-s3.port.iceberg=0', '-s3.port.lance=0'
    )

    # The gateway answers before a volume server has registered; an upload needs the master to hand out a file id.
    Wait-ServiceReady 'seaweedfs' {
        (Test-HttpResponds "http://127.0.0.1:$S3Port/") -and
        ((Invoke-RestMethod "http://127.0.0.1:$MasterPort/dir/assign" -TimeoutSec 5).fid)
    }
}
finally {
    Stop-Transcript | Out-Null
}
