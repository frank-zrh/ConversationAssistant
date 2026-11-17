#requires -Version 5.1
[CmdletBinding()]
param([switch] $Offline)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$AppDirectory = Split-Path $PSScriptRoot -Parent
$cache = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ConversationAssistant\BuildCache'
$archive = Join-Path $cache 'Microsoft.VCLibs.x64.14.00.Desktop.appx'
$archiveHash = 'B56A9101F706F9D95F815F5B7FA6EFBAC972E86573D378B96A07CFF5540C5961'
$runtimeFiles = @('concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
	'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll')

function Test-Hash([string] $Path, [string] $Algorithm, [string] $Expected) {
	return (Test-Path -LiteralPath $Path -PathType Leaf) -and
		((Get-Hash $Path $Algorithm) -eq $Expected)
}

function Get-Hash([string] $Path, [string] $Algorithm) {
	$stream = [IO.File]::OpenRead($Path)
	$hash = [Security.Cryptography.HashAlgorithm]::Create($Algorithm)
	try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
	finally { $hash.Dispose(); $stream.Dispose() }
}

New-Item -ItemType Directory -Path $cache -Force | Out-Null
if (-not (Test-Hash $archive 'SHA256' $archiveHash)) {
	if ($Offline) { throw 'The verified VC runtime archive is not cached. Run Prepare-Deployment.ps1 once with network access.' }
	$temporary = $archive + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
	try {
		Invoke-WebRequest -UseBasicParsing -Uri 'https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx' -OutFile $temporary
		if (-not (Test-Hash $temporary 'SHA256' $archiveHash)) { throw 'Microsoft VC runtime archive failed SHA-256 verification. Review and update the pin before accepting a new release.' }
		Move-Item -LiteralPath $temporary -Destination $archive -Force
	}
	finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$destination = Join-Path $AppDirectory 'Runtime\VC'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$staging = Join-Path $cache ([Guid]::NewGuid().ToString('N'))
try {
	[IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
	foreach ($name in $runtimeFiles) {
		$source = Join-Path $staging $name
		if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Pinned archive lacks $name" }
		$target = Join-Path $destination $name
		$hash = Get-Hash $source 'SHA256'
		if (-not (Test-Hash $target 'SHA256' $hash)) { Copy-Item -LiteralPath $source -Destination $target -Force }
	}
}
finally { if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force } }

$model = Join-Path $AppDirectory 'Models\ggml-small.bin'
if (-not (Test-Hash $model 'SHA1' '55356645C2B361A969DFD0EF2C5A50D530AFD8D5')) {
	if ($Offline) { throw 'The pinned Whisper model is missing or corrupt. Provision it before an offline build.' }
	New-Item -ItemType Directory -Path (Split-Path $model -Parent) -Force | Out-Null
	$temporary = $model + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
	try {
		Invoke-WebRequest -UseBasicParsing -Uri 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin' -OutFile $temporary
		if (-not (Test-Hash $temporary 'SHA1' '55356645C2B361A969DFD0EF2C5A50D530AFD8D5')) { throw 'Whisper model failed its pinned integrity check.' }
		Move-Item -LiteralPath $temporary -Destination $model -Force
	}
	finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

if ($Offline) {
	$speakerModels = @{
		'pyannote-segmentation-3.0.onnx' = '220AD67CA923BEF2FA91F2390C786097BF305BCEB5E261D4AF67B38E938E1079'
		'3dspeaker-eres2net.onnx' = '1A331345F04805BADBB495C775A6DDFFCDD1A732567D5EC8B3D5749E3C7A5E4B'
	}
	foreach ($entry in $speakerModels.GetEnumerator()) {
		if (-not (Test-Hash (Join-Path $AppDirectory ('Models\Speakers\' + $entry.Key)) 'SHA256' $entry.Value)) {
			throw "Missing or corrupt speaker model: $($entry.Key)"
		}
	}
}
else { & (Join-Path $AppDirectory 'Speech\Install-OfflineSpeakerModels.ps1') }
Write-Output 'Verified deployment payload: local VC runtimes, Whisper model and speaker models. Nothing is installed on the build PC.'
