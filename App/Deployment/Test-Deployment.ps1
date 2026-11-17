#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $Path)
$ErrorActionPreference = 'Stop'
function Get-Hash([string] $File, [string] $Algorithm) {
	$stream = [IO.File]::OpenRead($File)
	$hash = [Security.Cryptography.HashAlgorithm]::Create($Algorithm)
	try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
	finally { $hash.Dispose(); $stream.Dispose() }
}
$Path = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Publish directory does not exist: $Path" }

$executables = @(Get-ChildItem -LiteralPath $Path -Recurse -File | Where-Object {
	$_.Name -eq 'ConversationAssistant.App.exe' -or $_.Name -eq 'ConversationAssistant.App.exe.deploy'
})
if ($executables.Count -eq 0) { throw 'No application payload was found in the publish directory.' }
foreach ($executable in $executables) {
	$root = $executable.DirectoryName
	$files = @(Get-ChildItem -LiteralPath $root -Recurse -File)
	function Find-PayloadFile([string] $RelativePath) {
		foreach ($suffix in @('', '.deploy')) {
			$candidate = Join-Path $root ($RelativePath + $suffix)
			if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
		}
		throw "Missing required deployment file: $RelativePath in $root"
	}
	foreach ($required in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
		'Microsoft.UI.Xaml.dll', 'resources.pri', 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll',
		'msvcp140_codecvt_ids.dll', 'THIRD-PARTY-NOTICES.txt', 'Models\LICENSE-openai-whisper.txt',
		'Models\LICENSE-whisper-cpp.txt', 'Models\LICENSE-whisper-net.txt',
		'Models\Speakers\LICENSE-pyannote.txt', 'Models\Speakers\LICENSE-3dspeaker.txt',
		'Models\Speakers\LICENSE-sherpa-onnx.txt', 'Models\Speakers\LICENSE-onnxruntime.txt',
		'Models\Speakers\THIRD-PARTY-NOTICES-onnxruntime.txt')) { $null = Find-PayloadFile $required }
	$model = Find-PayloadFile 'Models\ggml-small.bin'
	if ((Get-Hash $model 'SHA1') -ne '55356645C2B361A969DFD0EF2C5A50D530AFD8D5') { throw 'Published Whisper model failed integrity validation.' }
	$speakerModels = @{
		'pyannote-segmentation-3.0.onnx' = '220AD67CA923BEF2FA91F2390C786097BF305BCEB5E261D4AF67B38E938E1079'
		'3dspeaker-eres2net.onnx' = '1A331345F04805BADBB495C775A6DDFFCDD1A732567D5EC8B3D5749E3C7A5E4B'
	}
	foreach ($entry in $speakerModels.GetEnumerator()) {
		$model = Find-PayloadFile ('Models\Speakers\' + $entry.Key)
		if ((Get-Hash $model 'SHA256') -ne $entry.Value) { throw "Published speaker model failed integrity validation: $($entry.Key)" }
	}
	foreach ($native in @('whisper.dll', 'ggml-cpu-whisper.dll', 'onnxruntime.dll', 'sherpa-onnx-c-api.dll')) {
		if (-not ($files | Where-Object { $_.Name -eq $native -or $_.Name -eq ($native + '.deploy') })) { throw "Missing native inference runtime: $native" }
	}
	foreach ($removed in @('Microsoft.CognitiveServices.Speech.core.dll', 'System.Speech.dll', 'workiq.exe', 'workiq.cmd')) {
		if ($files | Where-Object { $_.Name -eq $removed -or $_.Name -eq ($removed + '.deploy') }) { throw "Obsolete runtime found in deployment: $removed. Clean and republish." }
	}
	Write-Output "Verified self-contained Whisper/Work IQ HTTP payload: $root"
}
