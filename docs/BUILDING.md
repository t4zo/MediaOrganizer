# Compilar, testar e rodar

Este guia cobre o desenvolvimento a partir do código-fonte em **Windows, macOS e Linux**. Para apenas usar o app, baixe o executável pronto (veja o [README](../README.md#download-e-uso)).

## 1. Pré-requisitos

| | Windows | macOS | Linux |
| --- | --- | --- | --- |
| **.NET 10 SDK** | `winget install Microsoft.DotNet.SDK.10` | `brew install --cask dotnet-sdk` ou [instalador oficial](https://dotnet.microsoft.com/download/dotnet/10.0) | pacote da distro (ex.: `sudo apt install dotnet-sdk-10.0`) ou [`dotnet-install.sh`](https://learn.microsoft.com/dotnet/core/install/linux-scripted-manual) |
| **Git** | `winget install Git.Git` | `xcode-select --install` | `sudo apt install git` / `dnf install git` |
| **Bibliotecas gráficas** | — | — | Debian/Ubuntu: `sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libicu-dev`<br>Fedora: `sudo dnf install libX11 libICE libSM fontconfig libicu`<br>Arch: `sudo pacman -S libx11 libice libsm fontconfig icu` |

Confira a instalação:

```bash
dotnet --list-sdks   # deve listar uma versão 10.0.x
```

O arquivo `global.json` fixa o SDK 10 (qualquer 10.0.x mais novo também serve) e ativa o runner de testes *Microsoft.Testing.Platform*.

Editores recomendados: Visual Studio 2026, JetBrains Rider ou VS Code com a extensão C# Dev Kit + a extensão Avalonia (para pré-visualizar XAML).

## 2. Clonar, compilar e testar

```bash
git clone <url-do-repositório> MediaOrganizer
cd MediaOrganizer
dotnet restore
dotnet build
dotnet test --project tests/MediaOrganizer.Core.Tests
```

Os testes não acessam a internet: o TMDb é simulado. Eles cobrem o parser (com os casos de [`docs/test-cases.json`](test-cases.json)), legendas, conflitos, fingerprint, aplicar/desfazer e filmes, usando bibliotecas temporárias em disco.

## 3. Rodar em modo de desenvolvimento

```bash
dotnet run --project src/MediaOrganizer.App
```

Em desenvolvimento, os dados do app ficam em `data/` **na raiz do repositório** (ignorada pelo git). Para usar outra pasta:

```bash
# Windows (PowerShell)
$env:MEDIAORGANIZER_DATA = "C:\temp\mo-data"; dotnet run --project src/MediaOrganizer.App
# macOS / Linux
MEDIAORGANIZER_DATA=/tmp/mo-data dotnet run --project src/MediaOrganizer.App
```

> Dica: para testar sem mexer na sua biblioteca real, crie uma pasta com cópias **vazias** dos arquivos (só os nomes). No PowerShell:
> ```powershell
> Get-ChildItem E:\Series -Recurse -File | ForEach-Object {
>   $dest = $_.FullName.Replace('E:\Series', 'C:\temp\SeriesTeste')
>   New-Item -ItemType File -Force $dest | Out-Null }
> ```

## 4. Gerar os executáveis (um arquivo único por sistema)

Os scripts rodam os testes e publicam executáveis **autossuficientes** (não exigem .NET instalado) em `dist/`:

```bash
# Windows (PowerShell)
./build.ps1
# macOS / Linux
chmod +x build.sh && ./build.sh
```

| Saída | Sistema |
| --- | --- |
| `dist/windows/MediaOrganizer.exe` | Windows 10/11 x64 |
| `dist/linux/MediaOrganizer` | Linux x64 |
| `dist/macos/MediaOrganizer-arm64.app` | macOS 12+ Apple Silicon |
| `dist/macos/MediaOrganizer-x64.app` | macOS 12+ Intel |

Opções:

```bash
./build.ps1 -Targets win-x64 -Version 1.2.0      # PowerShell
VERSION=1.2.0 ./build.sh osx-arm64 linux-x64     # bash
```

Também é possível publicar manualmente um único sistema:

```bash
dotnet publish src/MediaOrganizer.App -c Release -r linux-x64 -o artifacts/publish/linux-x64
```

As propriedades de publicação ficam em `src/MediaOrganizer.App/MediaOrganizer.App.csproj` (`PublishSingleFile`, `SelfContained`, `IncludeNativeLibrariesForSelfExtract`, `EnableCompressionInSingleFile`).

### Cross-compile

Todos os alvos podem ser gerados a partir de **qualquer** sistema (ex.: gerar o `.app` do macOS no Windows). Observações:

- Gerado no Windows, o binário de Linux/macOS perde o bit de execução ao ser copiado — rode `chmod +x` no destino.
- O `build.sh` assina o `.app` *ad-hoc* (`codesign --sign -`) quando roda num Mac. Gerado em outro sistema, o usuário precisa usar *botão direito → Abrir* ou `xattr -dr com.apple.quarantine` na primeira execução.
- Para distribuir sem avisos de segurança é preciso assinar (Authenticode no Windows; Developer ID + notarização no macOS) — fora do escopo deste projeto.

## 5. Rodar os executáveis gerados

| Sistema | Como rodar |
| --- | --- |
| Windows | Duplo clique em `MediaOrganizer.exe` (SmartScreen: *Mais informações → Executar assim mesmo*). |
| macOS | Mova o `.app` para *Aplicativos*; na 1ª vez, *botão direito → Abrir*. Se necessário: `xattr -dr com.apple.quarantine MediaOrganizer-arm64.app`. |
| Linux | `chmod +x MediaOrganizer && ./MediaOrganizer` (dependências gráficas da seção 1). |

Na primeira execução é criada a pasta `data/` ao lado do executável (ou ao lado do `.app`).

## 6. Solução de problemas

| Sintoma | Causa / solução |
| --- | --- |
| `Testing with VSTest target is no longer supported` | Use `dotnet test --project ...` (o `global.json` já ativa o Microsoft.Testing.Platform). |
| `NETSDK1045` / SDK não encontrado | Instale o .NET 10 SDK. |
| Linux: `Unable to load shared library 'libSkiaSharp'` ou janela não abre | Instale as bibliotecas gráficas da seção 1. |
| Linux: erro de ICU (`Couldn't find a valid ICU package`) | Instale `libicu`/`icu` da sua distro. |
| macOS: "app está danificado" | `xattr -dr com.apple.quarantine <app>`. |
