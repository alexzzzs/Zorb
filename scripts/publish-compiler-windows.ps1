Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RootDir "seed/csharp/Zorb.Compiler.csproj"
$DriverEntry = Join-Path $RootDir "compiler/driver/main.zorb"
$Stage0OutputDir = Join-Path $RootDir "build/stage0-windows"
$Stage0Assembly = Join-Path $Stage0OutputDir "Zorb.Compiler.dll"
$BackendDir = Join-Path $RootDir "backend/llvm"
$OutputDir = if ($args.Count -ge 1) {
    $args[0]
} else {
    Join-Path $RootDir "artifacts/compiler/win-x64"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$Zig = if ($env:ZIG) { $env:ZIG } else { "zig" }
$LlvmPrefix = if ($env:LLVM_PREFIX) { $env:LLVM_PREFIX } else { Join-Path $env:ProgramFiles "LLVM" }
$ZigWindowsSystemLibraries = "ntdll.lib"
if (-not (Test-Path -LiteralPath $LlvmPrefix -PathType Container)) {
    throw "LLVM prefix '$LlvmPrefix' does not exist. Install LLVM 21 or set LLVM_PREFIX."
}
$LlvmLibDir = if ($env:LLVM_LIB_DIR) { $env:LLVM_LIB_DIR } else { Join-Path $LlvmPrefix "lib" }
$LlvmRuntimeDir = if ($env:LLVM_RUNTIME_DIR) { $env:LLVM_RUNTIME_DIR } else { Join-Path $LlvmPrefix "bin" }

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

function Invoke-CheckedCommand {
    param(
        [string]$Description,
        [string]$Command,
        [string[]]$Arguments
    )

    $commandOutput = & $Command @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $commandOutput | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        $details = ($commandOutput | ForEach-Object { $_.ToString() }) -join "`n"
        throw "$Description failed with exit code $exitCode.`n$details"
    }
}

$LlvmLibDir = Resolve-LlvmLibDir -PreferredDir $LlvmLibDir -SearchRoot $LlvmPrefix
$NormalizedLlvmPrefix = [System.IO.Path]::GetFullPath($LlvmPrefix).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$NormalizedLlvmLibDir = [System.IO.Path]::GetFullPath($LlvmLibDir)
$LlvmLibIsUnderPrefix = $NormalizedLlvmLibDir.StartsWith(
    $NormalizedLlvmPrefix + [System.IO.Path]::DirectorySeparatorChar,
    [System.StringComparison]::OrdinalIgnoreCase
)
if ($env:LLVM_LIB_DIR -and -not $LlvmLibIsUnderPrefix -and -not $env:LLVM_RUNTIME_DIR) {
    throw "LLVM_LIB_DIR is outside LLVM_PREFIX; set LLVM_RUNTIME_DIR to the matching LLVM-C.dll directory."
}

New-Item -ItemType Directory -Force -Path $Stage0OutputDir | Out-Null
dotnet build $ProjectPath --configuration Release --nologo --output $Stage0OutputDir
if ($LASTEXITCODE -ne 0) {
    throw "C# recovery stage build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $Stage0Assembly -PathType Leaf)) {
    throw "C# recovery stage assembly was not produced at '$Stage0Assembly'."
}

Push-Location $BackendDir
try {
    & $Zig build "-Doptimize=ReleaseSafe" "-Dllvm-prefix=$LlvmPrefix" "-Dllvm-lib-dir=$LlvmLibDir" "-Dllvm-library=LLVM-C"
    if ($LASTEXITCODE -ne 0) {
        throw "Zig LLVM backend build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$BackendApiCandidates = @(
    (Join-Path $BackendDir "zig-out/lib/zorb-llvm.lib"),
    (Join-Path $BackendDir "zig-out/lib/libzorb-llvm.a")
)
$BackendApi = $BackendApiCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $BackendApi) {
    throw "Zig build did not produce the static zorb-llvm API library."
}
$LlvmImportLibrary = Join-Path $LlvmLibDir "LLVM-C.lib"
$LlvmRuntimeLibrary = Join-Path $LlvmRuntimeDir "LLVM-C.dll"
if (-not (Test-Path -LiteralPath $LlvmRuntimeLibrary -PathType Leaf)) {
    throw "Unable to find LLVM-C.dll at '$LlvmRuntimeLibrary'. Set LLVM_RUNTIME_DIR to its directory."
}
$NativeLinkArgs = @($BackendApi, $LlvmImportLibrary, $ZigWindowsSystemLibraries)
$NativeFlags = "`"$BackendApi`" `"$LlvmImportLibrary`" $ZigWindowsSystemLibraries"
$CompilerOutput = Join-Path $OutputDir "zorb.exe"
$VerificationDir = Join-Path ([System.IO.Path]::GetTempPath()) ("zorb-release-fixed-point-" + [guid]::NewGuid())
$Generation1 = Join-Path $VerificationDir "zorb-generation-1.exe"
$Generation2 = Join-Path $VerificationDir "zorb-generation-2.exe"
$Generation3 = Join-Path $VerificationDir "zorb-generation-3.exe"

New-Item -ItemType Directory -Force -Path $VerificationDir | Out-Null
try {
    Copy-Item $LlvmRuntimeLibrary $VerificationDir -Force

    Invoke-CheckedCommand -Description "Generation-1 Zorb compiler build" -Command "dotnet" -Arguments @(
        $Stage0Assembly, "build", $DriverEntry, "--target", "host-windows", "-o", $Generation1,
        "--native-flags", $NativeFlags
    )

    $Generation2Arguments = @(
        "build", $DriverEntry, "--target", "host-windows", "-o", $Generation2, "--native-link-args"
    ) + $NativeLinkArgs
    Invoke-CheckedCommand -Description "Generation-2 Zorb compiler build" `
        -Command $Generation1 -Arguments $Generation2Arguments

    $Generation3Arguments = @(
        "build", $DriverEntry, "--target", "host-windows", "-o", $Generation3, "--native-link-args"
    ) + $NativeLinkArgs
    Invoke-CheckedCommand -Description "Generation-3 Zorb compiler build" `
        -Command $Generation2 -Arguments $Generation3Arguments

    $Generation2Hash = (Get-FileHash -LiteralPath $Generation2 -Algorithm SHA256).Hash
    $Generation3Hash = (Get-FileHash -LiteralPath $Generation3 -Algorithm SHA256).Hash
    if ($Generation2Hash -ne $Generation3Hash) {
        throw "Generation-2 and generation-3 compilers are not byte-identical."
    }

    Copy-Item $Generation2 $CompilerOutput -Force
    Copy-Item $LlvmRuntimeLibrary $OutputDir -Force
}
finally {
    Remove-Item -LiteralPath $VerificationDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Verified byte-identical generation-2 and generation-3 compilers."
Write-Host "Published Windows compiler to $OutputDir"
