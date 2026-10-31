# Checks a finished MSIX by unpacking it: the manifest, the app and its resources, and every icon the manifest names.
#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath, [string]$Version, [switch]$Store)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('wnr-msix-test-' + [guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($package, $work)
Write-Host "Extracted MSIX for validation: $work"
foreach ($name in @('AppxManifest.xml', 'resources.pri', 'WinNewsReader.exe', 'WinNewsReader.dll', 'WinNewsReader.pri', 'MicrosoftEdgeWebview2Setup.exe')) {
	if (-not (Test-Path -LiteralPath (Join-Path $work $name) -PathType Leaf)) { throw "The MSIX is missing $name." }
}
[xml]$manifest = Get-Content -LiteralPath (Join-Path $work 'AppxManifest.xml') -Raw
if ($Version -and $manifest.Package.Identity.Version -ne $Version) { throw "Manifest version $($manifest.Package.Identity.Version) is not $Version." }
if (-not $manifest.Package.Identity.Version.EndsWith('.0')) { throw 'The Store needs the fourth version part to be zero.' }
if ($Store) {
	[xml]$expectedManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
	foreach ($attribute in @('Name', 'Publisher')) {
		$expected = $expectedManifest.Package.Identity.GetAttribute($attribute)
		if ([string]::IsNullOrWhiteSpace($expected) -or $expected -match 'REPLACE' -or
			$manifest.Package.Identity.GetAttribute($attribute) -cne $expected) {
			throw "Store identity $attribute does not match Partner Center."
		}
	}
	if ($manifest.Package.Properties.PublisherDisplayName -cne $expectedManifest.Package.Properties.PublisherDisplayName) {
		throw 'Store publisher display name does not match Partner Center.'
	}
	if (Test-Path -LiteralPath (Join-Path $work 'AppxSignature.p7x')) {
		throw 'The Store submission MSIX must be unsigned; Partner Center signs it.'
	}
}
$visual = $manifest.GetElementsByTagName('VisualElements', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')[0]
foreach ($logo in @($manifest.Package.Properties.Logo, $visual.GetAttribute('Square44x44Logo'), $visual.GetAttribute('Square150x150Logo'))) {
	# Scale variants (Name.scale-200.png) stand in for the unqualified name.
	$base = [IO.Path]::GetFileNameWithoutExtension($logo)
	$found = Get-ChildItem -Path (Join-Path $work ([IO.Path]::GetDirectoryName($logo))) -Filter "$base*.png" -ErrorAction SilentlyContinue
	if (-not $found) { throw "No image for $logo in the MSIX." }
}
$bytes = [IO.File]::ReadAllBytes((Join-Path $work 'WinNewsReader.exe'))
if ([BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3c) + 4) -ne 0x8664) { throw 'WinNewsReader.exe is not x64.' }
Write-Host 'MSIX payload verified.'
