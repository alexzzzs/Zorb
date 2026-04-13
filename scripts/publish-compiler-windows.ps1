Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RootDir "Zorb.Compiler/Zorb.Compiler.csproj"
$OutputDir = if ($args.Count -ge 1) {
    $args[0]
} else {
    Join-Path $RootDir "artifacts/compiler/win-x64"
}
$Version = $env:VERSION
$InformationalVersion = $env:INFORMATIONAL_VERSION

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

Write-Host "Published Windows compiler to $OutputDir"
