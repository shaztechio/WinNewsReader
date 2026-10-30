# Signs files with the Certum code-signing certificate (SimplySign) and checks the signature and timestamp.
# -VerifyOnly checks without signing. The certificate is chosen by its SHA-1 thumbprint, CERTUM_KEY_ID.
# Velopack calls this for each file it packs: vpk pack --signTemplate "pwsh -NoProfile -File <this> -Path {{file}}".
#Requires -Version 7.0
[CmdletBinding()]
param(
	[Parameter(Mandatory)][string[]]$Path,
	[string]$Thumbprint = $env:CERTUM_KEY_ID,
	[string]$SignToolPath = $env:WNR_SIGNTOOL,
	[switch]$VerifyOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Authenticode signing requires Windows.' }
$Thumbprint = ($Thumbprint -replace '\s', '').ToUpperInvariant()
if ($Thumbprint -notmatch '^[0-9A-F]{40}$') { throw 'CERTUM_KEY_ID must be a SHA-1 certificate thumbprint (40 hex characters).' }
if (-not $SignToolPath) {
	$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
	$candidates = @(Get-ChildItem -Path "$sdk/*/x64/signtool.exe" -File |
		Sort-Object { [version]$_.Directory.Parent.Name } -Descending)
	if (-not $candidates.Count) { throw 'Install the Windows SDK x64 SignTool, or set WNR_SIGNTOOL.' }
	$SignToolPath = $candidates[0].FullName
}
$SignToolPath = (Resolve-Path -LiteralPath $SignToolPath).Path
$files = @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if (-not $VerifyOnly) {
	$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
	if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
		throw 'The signing certificate must be valid and connected to its private key.'
	}
	& $SignToolPath sign /sha1 $Thumbprint /fd SHA256 /tr http://time.certum.pl /td SHA256 @files
	if ($LASTEXITCODE -ne 0) { throw "SignTool signing failed (exit $LASTEXITCODE)." }
}
foreach ($file in $files) {
	$signature = Get-AuthenticodeSignature -LiteralPath $file
	if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
		throw "Invalid signature or unexpected publisher certificate: $file"
	}
	if (-not $signature.TimeStamperCertificate) { throw "Missing trusted timestamp: $file" }
	& $SignToolPath verify /pa /all /tw $file
	if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed (exit $LASTEXITCODE): $file" }
}
