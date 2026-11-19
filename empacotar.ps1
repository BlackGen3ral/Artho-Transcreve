param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Artho.Transcreve.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$extension = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'extensao/manifest.json') -Raw | ConvertFrom-Json
if ($version -ne $extension.version -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'O app e a extensão precisam ter a mesma versão.'
}

$bundle = Join-Path ([IO.Path]::GetTempPath()) ('artho-package-' + [guid]::NewGuid().ToString('N'))
New-Item -Path $bundle -ItemType Directory | Out-Null
try {
    dotnet publish $project -c Release -r win-x64 --self-contained false -p:DebugType=None -o (Join-Path $bundle 'app')
    if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação do app.' }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'extensao') -Destination $bundle -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $bundle
    $zip = Join-Path $bundle '..' ('artho-' + [guid]::NewGuid().ToString('N') + '.zip')
    $destination = Join-Path $OutputDirectory 'Artho-Transcreve-Windows.zip'
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($bundle, $zip)
        Move-Item -LiteralPath $zip -Destination $destination -Force
    } finally {
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
    }

    [ordered]@{
        version = $version
        sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        size = (Get-Item -LiteralPath $destination).Length
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'update.json') -Encoding utf8
} finally {
    $resolvedBundle = (Resolve-Path -LiteralPath $bundle).Path
    $resolvedTemp = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
    if ((Split-Path -Parent $resolvedBundle) -eq $resolvedTemp.TrimEnd('\', '/') -and
        (Split-Path -Leaf $resolvedBundle) -match '^artho-package-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolvedBundle -Recurse
    }
}
