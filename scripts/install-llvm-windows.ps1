param(
    [string]$Version = "22.1.8",
    [string]$ExpectedSha256 = "16e5709785fef73c854646241c4a92b5cd574318d1b33c63330dd7721903e55c",
    [int]$MaxDownloadAttempts = 4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) {
    throw "The LLVM Windows installer can only run on Windows."
}
if ($MaxDownloadAttempts -lt 1) {
    throw "MaxDownloadAttempts must be at least 1."
}

$InstallRoot = Join-Path $env:ProgramFiles "LLVM"
$Clang = Join-Path $InstallRoot "bin/clang.exe"
$ExpectedVersion = $Version

function Test-LlvmVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ClangPath,
        [Parameter(Mandatory = $true)]
        [string]$RequiredVersion
    )

    $VersionOutput = & $ClangPath --version
    $VersionExitCode = $LASTEXITCODE
    if ($VersionExitCode -ne 0 -or $null -eq $VersionOutput) {
        return $false
    }
    $VersionText = $VersionOutput -join [Environment]::NewLine
    return $VersionText.Contains("clang version $RequiredVersion")
}

function Find-LlvmCLibrary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LlvmRoot
    )

    return Get-ChildItem -Path $LlvmRoot -Filter LLVM-C.lib -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

if (Test-Path $Clang) {
    $HasExpectedVersion = Test-LlvmVersion -ClangPath $Clang -RequiredVersion $ExpectedVersion
    $LlvmCLibrary = Find-LlvmCLibrary -LlvmRoot $InstallRoot
    if ($HasExpectedVersion -and $null -ne $LlvmCLibrary) {
        Write-Host "LLVM $ExpectedVersion is already installed at $InstallRoot."
        exit 0
    }
}

$AssetName = "LLVM-$Version-win64.exe"
$AssetUrl = "https://github.com/llvm/llvm-project/releases/download/llvmorg-$Version/$AssetName"
$TempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$InstallerPath = Join-Path $TempRoot $AssetName

function Invoke-DownloadWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [Parameter(Mandatory = $true)]
        [string]$OutFile,
        [Parameter(Mandatory = $true)]
        [int]$Attempts
    )

    for ($Attempt = 1; $Attempt -le $Attempts; $Attempt += 1) {
        try {
            Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
            Write-Host "Downloading $Uri (attempt $Attempt of $Attempts)..."
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile
            return
        }
        catch {
            if ($Attempt -eq $Attempts) {
                throw
            }
            $DelaySeconds = [Math]::Pow(2, $Attempt)
            Write-Warning "LLVM download failed: $($_.Exception.Message). Retrying in $DelaySeconds seconds."
            Start-Sleep -Seconds $DelaySeconds
        }
    }
}

try {
    Invoke-DownloadWithRetry -Uri $AssetUrl -OutFile $InstallerPath -Attempts $MaxDownloadAttempts

    $ActualSha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ActualSha256 -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "LLVM installer checksum mismatch. Expected $ExpectedSha256, got $ActualSha256."
    }

    $Install = Start-Process -FilePath $InstallerPath -ArgumentList "/S" -Wait -PassThru
    if ($Install.ExitCode -ne 0 -and $Install.ExitCode -ne 3010) {
        throw "LLVM installer failed with exit code $($Install.ExitCode)."
    }

    if (-not (Test-Path $Clang)) {
        throw "LLVM installation did not create $Clang."
    }
    if (-not (Test-LlvmVersion -ClangPath $Clang -RequiredVersion $ExpectedVersion)) {
        throw "The LLVM installation at $InstallRoot does not report clang version $ExpectedVersion."
    }

    $LlvmCLibrary = Find-LlvmCLibrary -LlvmRoot $InstallRoot
    if ($null -eq $LlvmCLibrary) {
        throw "LLVM installation did not provide LLVM-C.lib under $InstallRoot."
    }

    Write-Host "Installed LLVM $ExpectedVersion at $InstallRoot."
}
finally {
    Remove-Item -LiteralPath $InstallerPath -Force -ErrorAction SilentlyContinue
}
