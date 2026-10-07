#requires -Version 5.1
<#
.SYNOPSIS
Explicitly provisions the licensed, hash-pinned offline speaker models (about 44 MiB).
.DESCRIPTION
Run before building the Windows x64 app. The app only reads these models; starting
listening never downloads anything. No recordings or voice embeddings are written.
The public sherpa-onnx ONNX exports include MIT-licensed pyannote segmentation
(Copyright 2022 CNRS) and Apache-2.0-licensed 3D-Speaker ERes2Net.
The upstream pyannote Hugging Face download requires registration; this script
uses sherpa-onnx's public redistribution, not that gated endpoint.
.EXAMPLE
powershell -NoProfile -File .\App\Speech\Install-OfflineSpeakerModels.ps1
.EXAMPLE
powershell -NoProfile -File .\App\Speech\Install-OfflineSpeakerModels.ps1 -Destination .\Tests\bin\Debug\net10.0\Models\Speakers
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $PSBoundParameters.ContainsKey('Destination')) {
    $Destination = Join-Path $PSScriptRoot '..\Models\Speakers'
}
$Destination = [IO.Path]::GetFullPath($Destination)
$files = @{
    'pyannote-segmentation-3.0.onnx' = '220AD67CA923BEF2FA91F2390C786097BF305BCEB5E261D4AF67B38E938E1079'
    '3dspeaker-eres2net.onnx' = '1A331345F04805BADBB495C775A6DDFFCDD1A732567D5EC8B3D5749E3C7A5E4B'
    'LICENSE-pyannote.txt' = '14D7016AD68E7394D6E6B78D96CC2AE431C905287B89674CFDF021E79E62B8BA'
    'LICENSE-3dspeaker.txt' = 'C71D239DF91726FC519C6EB72D318EC65820627232B2F796219E87DCF35D0AB4'
    'LICENSE-sherpa-onnx.txt' = 'CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30'
    'LICENSE-onnxruntime.txt' = '2F07C72751AED99790B8A4869CF2311DF85A860B22DED05FA22803587A48922C'
    'THIRD-PARTY-NOTICES-onnxruntime.txt' = '0E07B95F3A8D6230037707C5C4A2B554D12C4CB67369669AC255635528FFCEE2'
}

function Test-Hash([string] $Path, [string] $Sha256) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256)
}

function Get-PinnedFile([string] $Url, [string] $Path, [string] $Sha256) {
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path
    if (-not (Test-Hash $Path $Sha256)) {
        throw "Downloaded model or license failed SHA-256 validation. Nothing will be installed: $Url"
    }
}

$ready = $true
foreach ($entry in $files.GetEnumerator()) {
    if (-not (Test-Hash (Join-Path $Destination $entry.Key) $entry.Value)) { $ready = $false }
}
if ($ready) {
    Write-Output "Offline speaker models are already verified: $Destination"
    return
}

if (-not (Get-Command tar -ErrorAction SilentlyContinue)) {
    throw 'Windows tar.exe is required to extract the pinned segmentation archive. Install Windows 11 build tools and retry.'
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$staging = Join-Path $Destination ('.provision-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    $archive = Join-Path $staging 'segmentation.tar.bz2'
    Get-PinnedFile `
        'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2' `
        $archive '24615EE884C897D9D2BA09BB4D30DA6BB1B15E685065962DB5B02E76E4996488'
    & tar -xf $archive -C $staging `
        'sherpa-onnx-pyannote-segmentation-3-0/model.onnx' `
        'sherpa-onnx-pyannote-segmentation-3-0/LICENSE'
    if ($LASTEXITCODE -ne 0) { throw 'Unable to extract the offline segmentation model.' }
    Move-Item -LiteralPath (Join-Path $staging 'sherpa-onnx-pyannote-segmentation-3-0\model.onnx') `
        -Destination (Join-Path $staging 'pyannote-segmentation-3.0.onnx')
    Move-Item -LiteralPath (Join-Path $staging 'sherpa-onnx-pyannote-segmentation-3-0\LICENSE') `
        -Destination (Join-Path $staging 'LICENSE-pyannote.txt')
    Get-PinnedFile `
        'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx' `
        (Join-Path $staging '3dspeaker-eres2net.onnx') $files['3dspeaker-eres2net.onnx']
    Get-PinnedFile `
        'https://raw.githubusercontent.com/modelscope/3D-Speaker/065629c313eaf1a01c65c640c46d77e61e9607b4/LICENSE' `
        (Join-Path $staging 'LICENSE-3dspeaker.txt') $files['LICENSE-3dspeaker.txt']
    Get-PinnedFile `
        'https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/v1.13.8/LICENSE' `
        (Join-Path $staging 'LICENSE-sherpa-onnx.txt') $files['LICENSE-sherpa-onnx.txt']
    Get-PinnedFile `
        'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.28.2/LICENSE' `
        (Join-Path $staging 'LICENSE-onnxruntime.txt') $files['LICENSE-onnxruntime.txt']
    Get-PinnedFile `
        'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.28.2/ThirdPartyNotices.txt' `
        (Join-Path $staging 'THIRD-PARTY-NOTICES-onnxruntime.txt') $files['THIRD-PARTY-NOTICES-onnxruntime.txt']
    foreach ($entry in $files.GetEnumerator()) {
        if (-not (Test-Hash (Join-Path $staging $entry.Key) $entry.Value)) {
            throw "Extracted model or license failed SHA-256 validation: $($entry.Key)"
        }
    }
    foreach ($entry in $files.GetEnumerator()) {
        Move-Item -LiteralPath (Join-Path $staging $entry.Key) `
            -Destination (Join-Path $Destination $entry.Key) -Force
    }
    @'
Offline speaker models (CPU, Windows x64, mono 16 kHz)

Segmentation: pyannote/segmentation-3.0, MIT, Copyright (c) 2022 CNRS.
Model card: https://huggingface.co/pyannote/segmentation-3.0
Public ONNX distributor: https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-segmentation-models
SHA-256: 220AD67CA923BEF2FA91F2390C786097BF305BCEB5E261D4AF67B38E938E1079

Embedding: iic/speech_eres2net_base_sv_zh-cn_3dspeaker_16k, Apache-2.0.
Model card: https://modelscope.cn/models/iic/speech_eres2net_base_sv_zh-cn_3dspeaker_16k
Source: https://github.com/modelscope/3D-Speaker/tree/065629c313eaf1a01c65c640c46d77e61e9607b4
Public ONNX distributor: https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models
SHA-256: 1A331345F04805BADBB495C775A6DDFFCDD1A732567D5EC8B3D5749E3C7A5E4B

Runtime: org.k2fsa.sherpa.onnx 1.13.8 (Apache-2.0), installed by NuGet.
Its Windows x64 package includes ONNX Runtime 1.28.2 (MIT); see its license
and third-party notices in this directory.
These models estimate acoustic speaker turns, not real-world identities.
Embeddings are kept only in memory for a listening session, never persisted.
Labels may be wrong for similar voices, short turns, noise or overlapping speech.
This is speaker diarization, not source separation of simultaneous voices.
'@ | Set-Content -LiteralPath (Join-Path $Destination 'SOURCES.txt') -Encoding UTF8
    Write-Output "Verified offline speaker models installed in $Destination"
    Write-Output 'Rebuild or reinstall the app to include Models\Speakers and its licenses and notices.'
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
