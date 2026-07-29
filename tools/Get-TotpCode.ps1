# Dev helper: prints the current valid 6-digit TOTP code for a raw Base32 secret, so a local
# login can be completed without an authenticator app. Matches TotpService.cs's parameters
# (SHA1, 30s step, 6 digits, via OtpNet.Totp's defaults) using only built-in .NET crypto.
#
# Usage: powershell -File Get-TotpCode.ps1 -Base32Secret "<secret from DevSeedHostedService log>"

param(
    [Parameter(Mandatory = $true)]
    [string]$Base32Secret
)

function ConvertFrom-Base32 {
    param([string]$Base32)
    $alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
    $bits = ""
    foreach ($c in ($Base32.ToUpper().TrimEnd('=')).ToCharArray()) {
        $val = $alphabet.IndexOf($c)
        if ($val -lt 0) { throw "Invalid base32 character: $c" }
        $bits += [Convert]::ToString($val, 2).PadLeft(5, '0')
    }
    $byteCount = [Math]::Floor($bits.Length / 8)
    $bytes = New-Object byte[] $byteCount
    for ($i = 0; $i -lt $byteCount; $i++) {
        $bytes[$i] = [Convert]::ToByte($bits.Substring($i * 8, 8), 2)
    }
    return $bytes
}

function Get-TotpCode {
    param([byte[]]$Key, [int]$Step = 30, [int]$Digits = 6)
    $counter = [Math]::Floor((Get-Date).ToUniversalTime().Subtract([datetime]'1970-01-01').TotalSeconds / $Step)
    $counterBytes = [BitConverter]::GetBytes([Int64]$counter)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($counterBytes) }

    $hmac = New-Object System.Security.Cryptography.HMACSHA1(, $Key)
    $hash = $hmac.ComputeHash($counterBytes)

    $offset = $hash[$hash.Length - 1] -band 0x0F
    $binCode = (($hash[$offset] -band 0x7F) -shl 24) -bor `
               (($hash[$offset + 1] -band 0xFF) -shl 16) -bor `
               (($hash[$offset + 2] -band 0xFF) -shl 8) -bor `
               ($hash[$offset + 3] -band 0xFF)

    $code = $binCode % [Math]::Pow(10, $Digits)
    $secondsLeft = $Step - ((Get-Date).ToUniversalTime().Subtract([datetime]'1970-01-01').TotalSeconds % $Step)
    "{0:D6} (valid for {1:N0}s)" -f [int]$code, $secondsLeft
}

$keyBytes = ConvertFrom-Base32 -Base32 $Base32Secret
Get-TotpCode -Key $keyBytes
