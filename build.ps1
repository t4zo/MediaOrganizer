<#
.SYNOPSIS
  Gera um executável único e autossuficiente do MediaOrganizer para cada sistema.

.DESCRIPTION
  Saída em dist/:
    dist/windows/MediaOrganizer.exe
    dist/linux/MediaOrganizer
    dist/macos/MediaOrganizer-arm64.app  (Apple Silicon)
    dist/macos/MediaOrganizer-x64.app    (Intel)
  Não é preciso ter .NET instalado para rodar os executáveis gerados.

.EXAMPLE
  ./build.ps1                       # todos os sistemas
  ./build.ps1 -Targets win-x64      # só Windows
  ./build.ps1 -Version 1.2.0
#>
param(
    [string[]] $Targets = @('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64'),
    [string] $Version = '1.1.0',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src/MediaOrganizer.App/MediaOrganizer.App.csproj'
$dist = Join-Path $root 'dist'
$publishRoot = Join-Path $root 'artifacts/publish'

Write-Host "==> Testes" -ForegroundColor Cyan
dotnet test --project (Join-Path $root 'tests/MediaOrganizer.Core.Tests') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Os testes falharam; build interrompido.' }

foreach ($rid in $Targets) {
    Write-Host "==> Publicando $rid" -ForegroundColor Cyan
    $out = Join-Path $publishRoot $rid
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    dotnet publish $project -c $Configuration -r $rid -o $out -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $rid" }

    switch -Wildcard ($rid) {
        'win-*' {
            $target = Join-Path $dist 'windows'
            New-Item -ItemType Directory -Force $target | Out-Null
            Copy-Item (Join-Path $out 'MediaOrganizer.exe') $target -Force
        }
        'linux-*' {
            $target = Join-Path $dist 'linux'
            New-Item -ItemType Directory -Force $target | Out-Null
            Copy-Item (Join-Path $out 'MediaOrganizer') $target -Force
        }
        'osx-*' {
            $arch = $rid.Substring(4)
            $app = Join-Path $dist "macos/MediaOrganizer-$arch.app"
            if (Test-Path $app) { Remove-Item $app -Recurse -Force }
            $macos = Join-Path $app 'Contents/MacOS'
            $resources = Join-Path $app 'Contents/Resources'
            New-Item -ItemType Directory -Force $macos, $resources | Out-Null

            Copy-Item (Join-Path $out 'MediaOrganizer') $macos -Force
            Copy-Item (Join-Path $root 'build/macos/app.icns') $resources -Force
            (Get-Content (Join-Path $root 'build/macos/Info.plist') -Raw).Replace('$(VERSION)', ($Version -split '-')[0]) |
                Set-Content (Join-Path $app 'Contents/Info.plist') -Encoding utf8 -NoNewline
        }
    }
}

Write-Host "`n==> Pronto. Arquivos em $dist" -ForegroundColor Green
Get-ChildItem $dist -Recurse -File | Where-Object { $_.Name -in 'MediaOrganizer.exe', 'MediaOrganizer' } |
    ForEach-Object { '{0,-70} {1,8:N1} MB' -f $_.FullName.Substring($root.Length + 1), ($_.Length / 1MB) }
Write-Host "Obs.: no macOS/Linux, rode 'chmod +x' no executável se a permissão de execução se perder ao copiar." -ForegroundColor DarkGray
