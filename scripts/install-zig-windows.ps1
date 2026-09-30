$ErrorActionPreference = "Stop"

$zigVersion = "0.16.0"
$zigSigningKey = "RWSGOq2NVecA2UPNdBUZykf1CCb147pkmdtYxgb3Ti+JO/wCYvhbAb/U"
$archive = "zig-x86_64-windows-$zigVersion.zip"
$archiveUrl = "https://ziglang.org/download/$zigVersion/$archive"
$signaturePath = "$archive.minisig"
$archivePath = Join-Path (Get-Location) $archive

Invoke-WebRequest $archiveUrl -OutFile $archivePath
Invoke-WebRequest "$archiveUrl.minisig" -OutFile $signaturePath

$signatureLines = [System.IO.File]::ReadAllLines((Join-Path (Get-Location) $signaturePath))
if ($signatureLines.Length -ne 4 -or
    -not $signatureLines[0].StartsWith("untrusted comment: ") -or
    -not $signatureLines[2].StartsWith("trusted comment: ")) {
    throw "Zig Minisign file has an invalid format."
}

$trustedComment = $signatureLines[2].Substring("trusted comment: ".Length)
if ($trustedComment -notmatch "(^|[\t ])file:$([regex]::Escape($archive))([\t ]|$)") {
    throw "Zig signature trusted comment does not identify the expected archive."
}

$publicKey = [Convert]::FromBase64String($zigSigningKey)
$signatureRecord = [Convert]::FromBase64String($signatureLines[1])
$globalSignature = [Convert]::FromBase64String($signatureLines[3])
if ($publicKey.Length -ne 42 -or
    $publicKey[0] -ne 0x45 -or $publicKey[1] -ne 0x64 -or
    $signatureRecord.Length -ne 74 -or
    $signatureRecord[0] -ne 0x45 -or $signatureRecord[1] -ne 0x44 -or
    $globalSignature.Length -ne 64) {
    throw "Zig Minisign key or signature has an unexpected format."
}
for ($i = 0; $i -lt 8; $i++) {
    if ($publicKey[$i + 2] -ne $signatureRecord[$i + 2]) {
        throw "Zig signature key ID does not match the pinned public key."
    }
}

$openssl = (Get-Command openssl.exe -ErrorAction Stop).Source
$verificationRoot = $env:RUNNER_TEMP
if ([string]::IsNullOrWhiteSpace($verificationRoot)) {
    $verificationRoot = [IO.Path]::GetTempPath()
}
$verificationDirectory = Join-Path $verificationRoot "zorb-zig-minisign"
New-Item -ItemType Directory -Force -Path $verificationDirectory | Out-Null
$publicKeyPath = Join-Path $verificationDirectory "zig-public-key.der"
$archiveDigestPath = Join-Path $verificationDirectory "zig-archive.blake2b"
$archiveSignaturePath = Join-Path $verificationDirectory "zig-archive.ed25519"
$globalMessagePath = Join-Path $verificationDirectory "zig-comment-message"
$globalSignaturePath = Join-Path $verificationDirectory "zig-comment.ed25519"

# minisign's public-key record contains a 32-byte Ed25519 key after its 10-byte
# algorithm/key-ID header. Wrap the raw key in the standard SubjectPublicKeyInfo
# encoding expected by OpenSSL.
[byte[]]$spkiPrefix = 0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00
[byte[]]$publicKeyDer = New-Object byte[] ($spkiPrefix.Length + 32)
[Array]::Copy($spkiPrefix, 0, $publicKeyDer, 0, $spkiPrefix.Length)
[Array]::Copy($publicKey, 10, $publicKeyDer, $spkiPrefix.Length, 32)
[IO.File]::WriteAllBytes($publicKeyPath, $publicKeyDer)

[byte[]]$archiveSignature = New-Object byte[] 64
[Array]::Copy($signatureRecord, 10, $archiveSignature, 0, $archiveSignature.Length)
[IO.File]::WriteAllBytes($archiveSignaturePath, $archiveSignature)

# Current Zig signatures use minisign's ED format: Ed25519 over BLAKE2b-512.
$opensslArgs = @("dgst", "-blake2b512", "-binary", "-out", $archiveDigestPath, $archivePath)
& $openssl @opensslArgs
if ($LASTEXITCODE -ne 0) {
    throw "Unable to compute the Zig archive's Minisign BLAKE2b digest."
}
$opensslArgs = @("pkeyutl", "-verify", "-pubin", "-inkey", $publicKeyPath, "-keyform", "DER", "-rawin", "-in", $archiveDigestPath, "-sigfile", $archiveSignaturePath)
& $openssl @opensslArgs
if ($LASTEXITCODE -ne 0) {
    throw "Zig archive signature verification failed."
}

# Verify minisign's second signature too, so the trusted filename and hashed
# marker cannot be changed independently from the archive signature.
[byte[]]$trustedCommentBytes = [Text.Encoding]::UTF8.GetBytes($trustedComment)
[byte[]]$globalMessage = New-Object byte[] (64 + $trustedCommentBytes.Length)
[Array]::Copy($archiveSignature, 0, $globalMessage, 0, 64)
[Array]::Copy($trustedCommentBytes, 0, $globalMessage, 64, $trustedCommentBytes.Length)
[IO.File]::WriteAllBytes($globalMessagePath, $globalMessage)
[IO.File]::WriteAllBytes($globalSignaturePath, $globalSignature)
$opensslArgs = @("pkeyutl", "-verify", "-pubin", "-inkey", $publicKeyPath, "-keyform", "DER", "-rawin", "-in", $globalMessagePath, "-sigfile", $globalSignaturePath)
& $openssl @opensslArgs
if ($LASTEXITCODE -ne 0) {
    throw "Zig Minisign trusted-comment verification failed."
}

Expand-Archive -LiteralPath $archivePath -DestinationPath (Get-Location)
