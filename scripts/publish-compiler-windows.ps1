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
    & $Zig build "-Doptimize=ReleaseSafe" "-Dllvm-prefix=$LlvmPrefix" "-Dllvm-library=LLVM-C"
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
