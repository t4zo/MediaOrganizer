# Arquitetura

## Visão geral

```
┌──────────────────────── MediaOrganizer.App (Avalonia, MVVM) ────────────────────────┐
│ Views (.axaml)  ──binding──▶  ViewModels  ──▶  Services (Shell, Undo, PosterLoader) │
└───────────────────────────────────────┬──────────────────────────────────────────────┘
                                        │ usa apenas a API pública do Core
┌──────────────────────────── MediaOrganizer.Core (sem UI) ───────────────────────────┐
│ LibraryAnalyzer (organização) · CollectionAnalyzer (Coleção) · SubtitleAnalyzer      │
│ + SubtitleDownloader (Legendas)                                                      │
│   ├─ Collection: CollectionAnalyzer, CollectionExporter, SeriesReport/SeasonReport   │
│   ├─ Subtitles: SubtitleInventory, MatroskaReader, SubtitleScorer, SubtitleEmbedder, │
│   │             Providers (OpenSubtitles, SubDL, SubSource), SubtitleArchive         │
│   ├─ Scanning:  LibraryScanner, JunkDetector, FolderFingerprint                      │
│   ├─ Parsing:   ReleaseNameParser                                                    │
│   ├─ Metadata:  TmdbClient, MetadataCache, MediaMatcher, LanguagePolicy,             │
│   │             EpisodeTitles                                                        │
│   ├─ Naming:    FileNameSanitizer, NameFormatter                                     │
│   ├─ Planning:  RenamePlanner, SubtitleMatcher, ConflictDetector                     │
│   ├─ Execution: RenameExecutor, HistoryStore, RecycleBin                             │
│   └─ State:     DataPaths, SettingsStore, ProcessedStore, JsonStore                  │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

- **Core** não conhece a interface: pode ser reutilizado por uma CLI, um serviço ou testes. Todo I/O de rede passa por `TmdbClient` e pelos provedores de legenda (cada um recebe um `HttpClient`, o que permite simular os serviços nos testes).
- **App** só orquestra: mostra planos, coleta edições e chama o Core. Nenhuma regra de nomeação vive na UI.

## Fluxo principal

```
Scan ──▶ (fingerprint igual?) ──sim──▶ AlreadyProcessed ("nada foi alterado")
  │                 │
  │                não
  ▼                 ▼
LibraryFolder ──▶ Match (TMDb) ──▶ Plan (nomes + legendas + conflitos) ──▶ Prévia (edição)
                                                                              │
                                     Journal (antes) ◀── Apply ◀──────────────┘
                                          │
                                          ▼
                              Fingerprint + correspondência gravados
                                          │
                                          ▼
                                    Desfazer (journal)
```

1. **Scan** (`LibraryScanner`): lista séries/filmes, vídeos, legendas, lixo e pastas vazias.
2. **Fingerprint** (`ProcessedStore.IsUnchanged`): pula pastas que não mudaram desde o último processamento, sem rede.
3. **Match** (`MediaMatcher`): busca e pontua candidatos no TMDb; resultados incertos ficam como "precisa revisar".
4. **Plan** (`RenamePlanner`): calcula os novos nomes com `NameFormatter`/`FileNameSanitizer`, títulos com `LanguagePolicy`, legendas com `SubtitleMatcher` e marca conflitos com `ConflictDetector`.
5. **Prévia** (`OrganizeViewModel`/`FolderViewModel`): o usuário revisa, edita, desmarca, confirma ou troca a correspondência.
6. **Apply** (`RenameExecutor`): grava o journal, move os arquivos, registra o resultado; `LibraryAnalyzer.RecordApplied` grava correspondência e fingerprint.
7. **Undo** (`UndoService` → `RenameExecutor.Undo`): reverte pelo journal e invalida os fingerprints.

`LibraryAnalyzer.AnalyzeAsync` processa até 4 pastas em paralelo e devolve cada plano assim que fica pronto (`AsyncParallel.StreamAsync`: `IAsyncEnumerable` + `Channel`), para a lista ir aparecendo na tela.

## Fluxo da Coleção

```
Scan (mesmas regras) ──▶ correspondência memorizada? ──não──▶ busca do Organizar (confiável → memoriza; incerta → NoMatch)
                                   │ sim
                                   ▼
          tv/{id} (temporadas) ──▶ season/{n} (episódios + air_date) ──▶ SeriesReport
                                                                            │
                                     CollectionViewModel (lista + detalhe) ◀┘──▶ CollectionExporter (CSV/TXT)
```

`CollectionAnalyzer` reutiliza `LibraryScanner`, `ReleaseNameParser`, `EpisodeTitles` (títulos na política de idioma) e o `TmdbClient` com cache; "hoje" vem de um `TimeProvider` injetado (data fixa nos testes). A identificação continua centralizada nas correspondências do `SettingsStore`, compartilhadas com a tela Organizar; uma pasta nova é identificada por `LibraryAnalyzer.ResolveMatchAsync` (a mesma busca do Organizar), que só memoriza correspondências confiáveis.

## Fluxo das Legendas

```
Scan ──▶ correspondência memorizada? ──não──▶ busca do Organizar (confiável → memoriza; incerta → NoMatch)
              │ sim
              ▼
   IMDb ID (TMDb external_ids / movie) ──▶ SubtitleInventory: externas (SubtitleMatcher) + embutidas (MatroskaReader)
                                                     │ faltando
                                                     ▼
       SubtitleProviderSet: OpenSubtitles → SubDL → SubSource (só o que ainda falta) ──▶ SubtitleScorer ──▶ FolderSubtitlePlan
                                                                                                             │ prévia
                                                                                                             ▼
   SubtitleDownloader: journal (Create/Embed, Pending) ──▶ baixar ──▶ UTF-8 ──▶ arquivo oculto  ou  SubtitleEmbedder (mkvmerge)
```

- **Buscar não gasta cota; baixar gasta.** A análise só busca; o download acontece ao aplicar.
- Cada site implementa `ISubtitleProvider` e recebe seu próprio `HttpClient` (sem o handler de resiliência padrão: `ProviderHttp` não repete 429/406 de cota). Erros viram `SubtitleProviderException` (cota, limite, chave, indisponível); cota e chave tiram o site de uso na sessão (`SubtitleProviderSet`).
- `SubtitleEmbedder` nunca escreve sobre o vídeo: gera um temporário em `.mediaorganizer/tmp`, confere as faixas com o `MatroskaReader` e troca, guardando o original em `.mediaorganizer/originals`. O mkvmerge roda por trás de `IMkvMergeRunner` (falso nos testes).
- Desfazer: `RenameExecutor.Undo` trata `Create` (Lixeira) e delega `Embed` a `IEmbedUndo` (o `SubtitleEmbedder`).

## Decisões

| Decisão | Motivo |
| --- | --- |
| Avalonia UI 12 | Um único código nativo para Windows, macOS e Linux, com XAML e bindings compilados. |
| MVVM com CommunityToolkit.Mvvm | Geradores de código para propriedades/comandos; ViewModels testáveis, sem code-behind. |
| Diálogos sobrepostos (overlay) em vez de janelas | Comportamento idêntico nos três sistemas; ViewModels sem referência a janelas (`ShellServices`). |
| Journal gravado **antes** de mover | Mesmo com queda no meio, tudo o que foi feito pode ser desfeito. |
| Nunca sobrescrever / Lixeira em vez de apagar | Operações sobre a biblioteca do usuário precisam ser reversíveis. |
| Fingerprint sem data de modificação e com chave relativa | Funciona com o mesmo HD em sistemas diferentes. |
| Dados em `data/` ao lado do executável | App portátil; mesmo comportamento em todos os sistemas. |
| `System.Text.Json` com source generation | Sem reflexão: compatível com trimming e publicação em arquivo único. |
| `Microsoft.Extensions.Http.Resilience` | Retentativas com backoff para limites de taxa do TMDb (HTTP 429). |
| Casos de teste em JSON (`docs/test-cases.json`) | A mesma suíte valida a implementação C# e qualquer port futuro. |
| Leitor de MKV próprio (EBML) em vez de ffprobe | Saber as faixas embutidas sem dependência externa; o executável continua único. |
| mkvmerge externo (não embutido no app) | Embutir legendas é opcional; o MKVToolNix tem licença e atualizações próprias. |
| Legenda oculta sem mudar o nome | Players associam a legenda pelo nome; um ponto no início quebraria isso. |

## Estrutura de pastas

```
MediaOrganizer/
├─ src/
│  ├─ MediaOrganizer.Core/     regras de negócio (ver diagrama)
│  └─ MediaOrganizer.App/      Avalonia: Views/, ViewModels/, Services/, Styles/, Assets/
├─ tests/MediaOrganizer.Core.Tests/
├─ docs/                       esta documentação + test-cases.json + images/
├─ build/macos/                Info.plist e ícone do bundle .app
├─ build.ps1 / build.sh        geração dos executáveis
├─ Directory.Build.props       configurações comuns (net10.0, nullable, analisadores)
├─ Directory.Packages.props    versões centralizadas dos pacotes NuGet
└─ global.json                 SDK .NET 10 + runner de testes
```

## Como estender

- **Nova regra de nomeação**: altere `NameFormatter`/`FileNameSanitizer`, adicione casos em `docs/test-cases.json` e **incremente `FolderFingerprint.RulesVersion`** para que pastas já processadas sejam reavaliadas.
- **Nova fonte de metadados** (ex.: TVmaze): crie um cliente com a mesma forma de `TmdbClient` e use-o em `MediaMatcher`/`RenamePlanner`.
- **Novo site de legendas**: implemente `ISubtitleProvider` (busca + download, erros como `SubtitleProviderException`), registre o `HttpClient` e inclua na lista do `SubtitleProviderSet` em `ServiceRegistration`; use `SubtitleArchive` se o site entregar ZIP.
- **Nova funcionalidade de biblioteca** (a Coleção e as Legendas são exemplos completos): acrescente um serviço no Core consumindo `LibraryScanner`/`LibraryFolder` e uma nova página na App (ViewModel + View + item em `MainWindowViewModel.NavItems` + `DataTemplate` em `App.axaml`).
