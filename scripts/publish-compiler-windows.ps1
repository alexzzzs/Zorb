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

$LlvmLibDir = Resolve-LlvmLibDir -PreferredDir $LlvmLibDir -SearchRoot $LlvmPrefix

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
$NativeFlags = "`"$BackendApi`" `"$LlvmImportLibrary`" $ZigWindowsSystemLibraries"
$CompilerOutput = Join-Path $OutputDir "zorb.exe"
& dotnet $Stage0Assembly build $DriverEntry --target host-windows -o $CompilerOutput --native-flags $NativeFlags
if ($LASTEXITCODE -ne 0) {
    throw "Integrated Zorb compiler build failed with exit code $LASTEXITCODE."
}

Copy-Item (Join-Path $LlvmPrefix "bin/LLVM-C.dll") $OutputDir -Force

Write-Host "Published Windows compiler to $OutputDir"
