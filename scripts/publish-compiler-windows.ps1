Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RootDir "Zorb.Compiler/Zorb.Compiler.csproj"
$BackendDir = Join-Path $RootDir "Zorb.LlvmBackend"
$OutputDir = if ($args.Count -ge 1) {
    $args[0]
} else {
    Join-Path $RootDir "artifacts/compiler/win-x64"
}
$Version = $env:VERSION
$InformationalVersion = $env:INFORMATIONAL_VERSION
$Zig = if ($env:ZIG) { $env:ZIG } else { "zig" }
$LlvmPrefix = if ($env:LLVM_PREFIX) { $env:LLVM_PREFIX } else { Join-Path $env:ProgramFiles "LLVM" }
if (-not (Test-Path -LiteralPath $LlvmPrefix -PathType Container)) {
    throw "LLVM prefix '$LlvmPrefix' does not exist. Install LLVM 20 or set LLVM_PREFIX."
}
$LlvmIncludeDir = if ($env:LLVM_INCLUDE_DIR) { $env:LLVM_INCLUDE_DIR } else { Join-Path $LlvmPrefix "include" }
$LlvmLibDir = if ($env:LLVM_LIB_DIR) { $env:LLVM_LIB_DIR } else { Join-Path $LlvmPrefix "lib" }

function Resolve-LlvmHeaderIncludeDir {
    param(
        [string]$PreferredDir,
        [string]$SearchRoot
    )

    $analysisHeader = Join-Path $PreferredDir "llvm-c/Analysis.h"
    if (Test-Path -LiteralPath $analysisHeader -PathType Leaf) {
        return $PreferredDir
    }

    $discoveredHeader = Get-ChildItem -Path $SearchRoot -Filter Analysis.h -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*llvm-c\Analysis.h" } |
        Select-Object -First 1
    if ($null -eq $discoveredHeader) {
        throw "Unable to find llvm-c/Analysis.h under '$SearchRoot'. Set LLVM_INCLUDE_DIR explicitly."
    }

    return Split-Path (Split-Path $discoveredHeader.FullName -Parent) -Parent
}

function Resolve-LlvmLibDir {
    param(
        [string]$PreferredDir,
        [string]$SearchRoot
    )

    $importLibrary = Join-Path $PreferredDir "LLVM-C.lib"
    if (Test-Path -LiteralPath $importLibrary -PathType Leaf) {
        return $PreferredDir
    }

    $discoveredLibrary = Get-ChildItem -Path $SearchRoot -Filter LLVM-C.lib -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $discoveredLibrary) {
        throw "Unable to find LLVM-C.lib under '$SearchRoot'. Set LLVM_LIB_DIR explicitly."
    }

    return Split-Path $discoveredLibrary.FullName -Parent
}

$LlvmIncludeDir = Resolve-LlvmHeaderIncludeDir -PreferredDir $LlvmIncludeDir -SearchRoot $LlvmPrefix
$LlvmLibDir = Resolve-LlvmLibDir -PreferredDir $LlvmLibDir -SearchRoot $LlvmPrefix

$PublishArgs = @(
  $ProjectPath
  "-c", "Release"
  "-r", "win-x64"
  "--self-contained", "true"
  "/p:PublishSingleFile=true"
  "/p:PublishTrimmed=false"
  "-o", $OutputDir
)

if ($Version) {
    $PublishArgs += "/p:Version=$Version"
}

if ($InformationalVersion) {
    $PublishArgs += "/p:InformationalVersion=$InformationalVersion"
}

dotnet publish @PublishArgs

Push-Location $BackendDir
try {
    & $Zig build "-Doptimize=ReleaseSafe" "-Dllvm-prefix=$LlvmPrefix" "-Dllvm-include-dir=$LlvmIncludeDir" "-Dllvm-lib-dir=$LlvmLibDir" "-Dllvm-library=LLVM-C"
    if ($LASTEXITCODE -ne 0) {
        throw "Zig LLVM backend build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Copy-Item (Join-Path $BackendDir "zig-out/bin/zorb-llvm-backend.exe") $OutputDir -Force
Copy-Item (Join-Path $LlvmPrefix "bin/LLVM-C.dll") $OutputDir -Force
Copy-Item (Join-Path $LlvmPrefix "bin/ld.lld.exe") $OutputDir -Force

Write-Host "Published Windows compiler to $OutputDir"
