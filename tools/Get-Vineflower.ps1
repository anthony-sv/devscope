<#
.SYNOPSIS
  Downloads the Vineflower Java decompiler from Maven Central and verifies its SHA-1.

.DESCRIPTION
  The jar is saved to tools\vineflower\. The build copies it to <output>\tools\, where Devscope
  finds it automatically. Vineflower is Apache-2.0 licensed: https://github.com/Vineflower/vineflower
#>
param(
  [string]$Version = '1.12.0'
)

$ErrorActionPreference = 'Stop'
$destination = Join-Path $PSScriptRoot 'vineflower'
New-Item -ItemType Directory -Force -Path $destination | Out-Null

$baseUrl = "https://repo1.maven.org/maven2/org/vineflower/vineflower/$Version/vineflower-$Version.jar"
$jar = Join-Path $destination "vineflower-$Version.jar"
$temp = "$jar.download"

Invoke-WebRequest -UseBasicParsing -Uri $baseUrl -OutFile $temp
$expected = (Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl.sha1").Content
if ($expected -is [byte[]]) { $expected = [Text.Encoding]::ASCII.GetString($expected) }
$expected = $expected.Trim().Substring(0, 40).ToLowerInvariant()
$actual = (Get-FileHash -Algorithm SHA1 -Path $temp).Hash.ToLowerInvariant()

if ($expected -ne $actual) {
  Remove-Item $temp -Force
  throw "Checksum mismatch for vineflower-$Version.jar (expected $expected, got $actual)."
}

# Keep a single version so auto-detection is unambiguous.
Get-ChildItem $destination -Filter 'vineflower-*.jar' | Remove-Item -Force
Move-Item $temp $jar
Write-Host "Saved $jar (SHA-1 verified)."
