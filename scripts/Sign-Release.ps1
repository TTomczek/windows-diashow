param(
    [Parameter(Mandatory = $true)]
    [string]$Executable
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:DIASHOW_ED25519_PRIVATE_KEY)) {
    throw 'DIASHOW_ED25519_PRIVATE_KEY must be configured for master releases.'
}

$executablePath = (Resolve-Path $Executable).Path
$directory = Split-Path $executablePath
$name = Split-Path $executablePath -Leaf
$checksumPath = Join-Path $directory "$name.sha256"
$signaturePath = Join-Path $directory "$name.sig"
$keyPath = Join-Path $env:RUNNER_TEMP 'diashow-ed25519-key.pem'
$signatureBinaryPath = Join-Path $env:RUNNER_TEMP 'diashow-ed25519-signature.bin'

try {
    $privateKey = $env:DIASHOW_ED25519_PRIVATE_KEY
    if ($privateKey -notmatch '\r?\n' -and $privateKey.Contains('\n')) {
        $privateKey = $privateKey.Replace('\n', "`n")
    }

    [IO.File]::WriteAllText(
        $keyPath,
        $privateKey,
        [Text.UTF8Encoding]::new($false))

    & openssl pkey -in $keyPath -noout
    if ($LASTEXITCODE -ne 0) {
        throw 'DIASHOW_ED25519_PRIVATE_KEY is not a readable PEM private key.'
    }

    $hash = (Get-FileHash -Algorithm SHA256 $executablePath).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        $checksumPath,
        "$hash  $name`n",
        [Text.UTF8Encoding]::new($false))

    & openssl pkeyutl -sign -rawin -inkey $keyPath -in $checksumPath -out $signatureBinaryPath
    if ($LASTEXITCODE -ne 0) {
        throw 'OpenSSL failed to create the Ed25519 signature.'
    }

    $signature = [Convert]::ToBase64String([IO.File]::ReadAllBytes($signatureBinaryPath))
    [IO.File]::WriteAllText(
        $signaturePath,
        "$signature`n",
        [Text.UTF8Encoding]::new($false))
}
finally {
    Remove-Item $keyPath, $signatureBinaryPath -Force -ErrorAction SilentlyContinue
}
