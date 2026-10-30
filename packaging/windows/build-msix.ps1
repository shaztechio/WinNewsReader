# Builds WinNewsReader's win-x64 app as an unsigned MSIX for the Microsoft Store. Partner Center signs it on submission.
# See packaging/windows/README.md.
#Requires -Version 7.0
[CmdletBinding()]
param(
	[switch]$LocalTest,
	[string]$PublishDirectory,
	[string]$OutputDirectory,
	[string]$SdkBinPath,
	[string]$Version
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'MSIX packaging requires Windows.' }

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$packageName = [string]$manifest.Package.Identity.Name
$publisher = [string]$manifest.Package.Identity.Publisher
$publisherDisplayName = [string]$manifest.Package.Properties.PublisherDisplayName
$displayName = [string]$manifest.Package.Properties.DisplayName
if ($LocalTest) {
	$packageName = 'WinNewsReader.LocalTest'
	$publisher = 'CN=WinNewsReader Local Test'
	$publisherDisplayName = 'WinNewsReader'
	$displayName = 'WinNewsReader (Local Test)'
}
elseif ("$packageName$publisher$publisherDisplayName" -match 'REPLACE') {
	throw 'AppxManifest.xml still has placeholder identity. Paste the Product identity from Partner Center, or use -LocalTest.'
}

# The SDK tools can come from the Windows SDK or from the Microsoft.Windows.SDK.BuildTools package the app already restores.
if (-not $SdkBinPath) {
	$roots = @((Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'), (Join-Path $HOME '.nuget/packages/microsoft.windows.sdk.buildtools'))
	$candidates = foreach ($r in $roots) {
		Get-ChildItem -LiteralPath $r -Directory -ErrorAction SilentlyContinue | ForEach-Object {
			Get-ChildItem -Path "$($_.FullName)/x64", "$($_.FullName)/bin/*/x64" -Directory -ErrorAction SilentlyContinue
		}
	}
	$SdkBinPath = $candidates | Where-Object { (Test-Path "$($_.FullName)/MakeAppx.exe") -and (Test-Path "$($_.FullName)/MakePri.exe") } |
		Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $SdkBinPath) { throw 'Install the Windows SDK or supply -SdkBinPath containing MakeAppx.exe and MakePri.exe.' }
$makeAppx = Join-Path $SdkBinPath 'MakeAppx.exe'
$makePri = Join-Path $SdkBinPath 'MakePri.exe'

if (-not $Version) {
	$Version = (Select-Xml -Path (Join-Path $root 'src/App/WinNewsReader.App.csproj') -XPath '//Version').Node.InnerText
}
if ($Version -notmatch '^(\d+\.\d+\.\d+)') { throw "Expected a version like 1.2.3, got '$Version'." }
$versionPrefix = $Matches[1]
$packageVersion = [version]"$versionPrefix.0"   # The Store requires the fourth part to stay zero.
if ($packageVersion.Major -lt 1) { throw 'The Store needs a major version of at least 1.' }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/msix' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
# A fresh staging folder each run, kept for inspection. Nothing the caller supplied is deleted.
$runDirectory = Join-Path $OutputDirectory ([guid]::NewGuid().ToString('N'))
$stage = Join-Path $runDirectory 'package'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
if (-not $PublishDirectory) {
	$PublishDirectory = Join-Path $runDirectory 'publish'
	& dotnet publish (Join-Path $root 'src/App') -c Release -r win-x64 --self-contained -p:Platform=x64 -p:Version=$versionPrefix -o $PublishDirectory --nologo
	if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($required in @('WinNewsReader.exe', 'WinNewsReader.dll', 'WinNewsReader.pri')) {
	if (-not (Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) {
		throw "The publish folder is missing $required. Supply a self-contained win-x64 publish of src/App."
	}
}
Get-ChildItem -LiteralPath $publish | Copy-Item -Destination $stage -Recurse
Copy-Item -Path (Join-Path $PSScriptRoot 'Assets/*') -Destination (New-Item -ItemType Directory -Path (Join-Path $stage 'Assets') -Force) -Recurse -Force

$manifest.Package.Identity.SetAttribute('Name', $packageName)
$manifest.Package.Identity.SetAttribute('Publisher', $publisher)
$manifest.Package.Identity.SetAttribute('Version', $packageVersion.ToString())
$manifest.Package.Properties.DisplayName = $displayName
$manifest.Package.Properties.PublisherDisplayName = $publisherDisplayName
$visual = $manifest.GetElementsByTagName('VisualElements', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')[0]
$visual.SetAttribute('DisplayName', $displayName)
$manifestPath = Join-Path $stage 'AppxManifest.xml'
$manifest.Save($manifestPath)

# Index the scale and target-size variants so Windows picks a crisp icon for each surface.
$imageRoot = Join-Path $runDirectory 'images'
New-Item -ItemType Directory -Path $imageRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $stage 'Assets') -Destination $imageRoot -Recurse
$priConfig = Join-Path $runDirectory 'priconfig.xml'
& $makePri createconfig /cf $priConfig /dq en-US /o
if ($LASTEXITCODE -ne 0) { throw 'MakePri createconfig failed.' }
[xml]$config = Get-Content -LiteralPath $priConfig -Raw
$null = $config.resources.RemoveChild($config.resources.packaging)   # One package, every scale: no resource packs.
$config.Save($priConfig)
# Only the package's images are indexed. Indexing the whole folder also picks up the Windows App SDK's own assets, which clash.
& $makePri new /pr $imageRoot /cf $priConfig /mn $manifestPath /of (Join-Path $stage 'resources.pri') /o
if ($LASTEXITCODE -ne 0) { throw 'MakePri resource indexing failed.' }

$suffix = if ($LocalTest) { '-local-test' } else { '-store' }
$packagePath = Join-Path $OutputDirectory "WinNewsReader-$versionPrefix-win-x64$suffix.msix"
& $makeAppx pack /d $stage /p $packagePath /o
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx packaging failed.' }
& (Join-Path $PSScriptRoot 'test-msix.ps1') -PackagePath $packagePath -Version $packageVersion.ToString()
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($packagePath))" | Set-Content -LiteralPath "$packagePath.sha256" -Encoding ascii
Write-Host "Unsigned MSIX: $packagePath"
if ($LocalTest) { Write-Warning 'Local-test identity: not a Store submission. Sign it with a test certificate and trust that before installing.' }
