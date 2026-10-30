# Builds a local-test MSIX, signs it with a throwaway certificate, and writes a Windows Sandbox config that installs and starts it.
# Output: artifacts/sandbox (the MSIX, the certificate's public half, install.ps1 and WinNewsReader.wsb). Double-click the .wsb.
# The private key is deleted when this finishes, and the Sandbox is disposable, so nothing is trusted on this PC.
#Requires -Version 7.0
[CmdletBinding()]
param([string]$PublishDirectory, [string]$SignToolPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows only.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out = Join-Path $root 'artifacts/sandbox'
New-Item -ItemType Directory -Path $out -Force | Out-Null
Get-ChildItem -LiteralPath $out -File | Remove-Item -Force

$buildArgs = @{ LocalTest = $true; OutputDirectory = (Join-Path $out 'build') }
if ($PublishDirectory) { $buildArgs.PublishDirectory = $PublishDirectory }
& (Join-Path $PSScriptRoot 'build-msix.ps1') @buildArgs
$msix = Get-ChildItem -LiteralPath (Join-Path $out 'build') -Filter '*-local-test.msix' | Select-Object -First 1
Copy-Item -LiteralPath $msix.FullName -Destination $out

if (-not $SignToolPath) {
	$SignToolPath = Get-ChildItem -Path "${env:ProgramFiles(x86)}/Windows Kits/10/bin/*/x64/signtool.exe" |
		Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $SignToolPath) { throw 'Install the Windows SDK or pass -SignToolPath.' }

$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=WinNewsReader Local Test' -KeyUsage DigitalSignature `
	-FriendlyName 'WinNewsReader Local Test' -CertStoreLocation 'Cert:\CurrentUser\My' `
	-TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
try {
	Export-Certificate -Cert $cert -FilePath (Join-Path $out 'WinNewsReaderTest.cer') | Out-Null
	$package = Join-Path $out $msix.Name
	& $SignToolPath sign /fd SHA256 /sha1 $cert.Thumbprint $package
	if ($LASTEXITCODE -ne 0) { throw 'SignTool failed.' }
}
finally {
	Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
}

Set-Content -LiteralPath (Join-Path $out 'install.ps1') -Encoding utf8 -Value @"
`$ErrorActionPreference = 'Stop'
Import-Certificate -FilePath C:\wnr\WinNewsReaderTest.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Add-AppxPackage C:\wnr\$($msix.Name)
`$package = Get-AppxPackage WinNewsReader.LocalTest
Write-Host "Installed `$(`$package.PackageFullName)"
Start-Process explorer.exe "shell:AppsFolder\`$(`$package.PackageFamilyName)!WinNewsReader"
"@
Set-Content -LiteralPath (Join-Path $out 'WinNewsReader.wsb') -Encoding utf8 -Value @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$out</HostFolder>
      <SandboxFolder>C:\wnr</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoExit -ExecutionPolicy Bypass -File C:\wnr\install.ps1</Command>
  </LogonCommand>
</Configuration>
"@
Write-Host "Ready: $(Join-Path $out 'WinNewsReader.wsb')"
