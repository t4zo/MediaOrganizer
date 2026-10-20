# Guia para portar para Go ou Rust

O comportamento está todo descrito em [SPEC.md](SPEC.md) e verificado por [`test-cases.json`](test-cases.json). Um port é considerado correto quando:

1. passa em **todos** os casos de `test-cases.json` (parser, filmes, sanitização, formato, idioma de legendas);
2. reproduz os cenários de `tests/MediaOrganizer.Core.Tests/WorkflowTests.cs` (renomear com idioma original, série não latina, subpastas RARBG, "nada foi alterado", desfazer, conflitos, filmes);
3. lê e grava `settings.json`, `processed.json` e `history/*.json` **no mesmo formato**, para que as versões possam conviver com a mesma pasta `data/`.

## Mapa de componentes

| Componente (C#) | Responsabilidade | Go | Rust |
| --- | --- | --- | --- |
| `ReleaseNameParser` | Regex de SxxExx, ano, tags | `regexp` (RE2: sem lookbehind — ver nota) | `regex` (sem lookbehind) ou `fancy-regex` |
| `FileNameSanitizer`, `NameFormatter` | Nomes finais | `strings`, `fmt` | `String`, `format!` |
| `LanguagePolicy` | Idioma dos títulos | `map[string]struct{}` | `phf`/`HashSet` |
| `TmdbClient` | HTTP + JSON | `net/http`, `encoding/json` | `reqwest`, `serde`/`serde_json` |
| Retentativas | 429/erros transitórios | `hashicorp/go-retryablehttp` | `reqwest-retry` / `backon` |
| `MetadataCache` | Cache em disco | `os`, `crypto` ou xxhash | `std::fs`, `xxhash-rust` |
| `MediaMatcher.Normalize` | Remover acentos (NFD) | `golang.org/x/text/unicode/norm` + `unicode.Mn` | `unicode-normalization` |
| `LibraryScanner` | Varredura | `os.ReadDir`, `filepath` | `std::fs::read_dir`, `walkdir` |
| `JunkDetector` | Curingas `*`/`?` | `filepath.Match` (minúsculas) | `glob::Pattern` / `wildmatch` |
| `FolderFingerprint` | XxHash128 | `github.com/zeebo/xxh3` (`Hash128`) | `xxhash-rust` (`xxh3_128`) |
| `RenameExecutor` | Mover sem sobrescrever | `os.Rename` + checar existência antes | `std::fs::rename` + checar existência |
| `RecycleBin` | Lixeira | Windows: `golang.org/x/sys/windows` (SHFileOperation); mac/Linux: igual ao C# | crate `trash` (cobre os 3 sistemas) |
| `JsonStore` | Gravação atômica | escrever `.tmp` + `os.Rename` | escrever `.tmp` + `fs::rename` |
| UI | Desktop multiplataforma | Wails (HTML/JS) ou Fyne | Tauri (HTML/JS) ou Slint/egui |
| `LibraryAnalyzer.AnalyzeAsync` | Paralelismo | goroutines + `errgroup` com limite 4 + canal | `tokio` + `futures::stream::buffer_unordered(4)` |

## Pontos de atenção

- **Lookbehind**: a regex de separadores `(?<!\d)\.|\.(?!\d)` não existe em RE2 (Go) nem no crate `regex`. Implemente com um laço: troque `.` por espaço a menos que o caractere anterior **e** o seguinte sejam dígitos.
- **`\b` e Unicode**: em .NET, `\b` considera letras Unicode. Em Go use `(?i)` e teste nomes acentuados; em Rust o `regex` já é Unicode por padrão.
- **XxHash128**: o .NET `System.IO.Hashing.XxHash128` implementa **XXH3-128** com seed 0; o resultado é gravado como hex minúsculo dos 16 bytes em ordem *big-endian* (`GetCurrentHash()` do .NET). Confirme com um caso conhecido antes de confiar no hash — se divergir, os usuários verão as pastas reanalisadas uma vez (não há perda de dados).
- **Comparação de nomes**: destinos são comparados sem diferenciar maiúsculas (Windows/macOS); uma troca só de caixa precisa do passo intermediário `.mo-tmp`.
- **Ordem das operações**: arquivos antes de pastas; desfazer em ordem inversa.
- **Enums em JSON**: gravados como texto (`"Series"`, `"Done"`), propriedades em camelCase.
- **Datas**: ISO-8601 com fuso (`2026-10-19T23:51:01-03:00`).

## Sugestão de ordem para o port

1. Parser + sanitização + formatação (validar com `test-cases.json`).
2. Cliente TMDb com cache e um TMDb falso para testes (ver `FakeTmdb` em `TestSupport.cs`).
3. Scanner, legendas, conflitos, planner.
4. Executor + journal + fingerprint (validar compatibilidade dos JSON com a versão C#).
5. Interface.
