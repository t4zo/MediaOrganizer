# Como contribuir

Obrigado pelo interesse! Este guia resume como o projeto é organizado e o que se espera de uma contribuição.

## Ambiente

Siga [docs/BUILDING.md](docs/BUILDING.md) para instalar o .NET 10 e compilar. Resumo:

```bash
dotnet build
dotnet test --project tests/MediaOrganizer.Core.Tests
dotnet run --project src/MediaOrganizer.App
```

## Estrutura

- `src/MediaOrganizer.Core` — todas as regras (parser, TMDb, planejamento, execução). **Sem dependência de UI.**
- `src/MediaOrganizer.App` — interface Avalonia em MVVM (`Views/*.axaml` + `ViewModels/`).
- `tests/MediaOrganizer.Core.Tests` — testes xUnit; o TMDb é simulado (`FakeTmdb`).
- `docs/` — documentação e `test-cases.json`.

Detalhes em [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Padrões de código

- C# moderno, `nullable` habilitado, analisadores `latest-recommended` — o build deve ficar **sem avisos**.
- Siga o estilo do `.editorconfig` (namespaces de arquivo, `var`, expressões de coleção).
- Comentários e textos de interface em **português**; nomes de código em inglês.
- Regras de negócio sempre no Core, nunca em ViewModels.
- XAML com bindings compilados: declare `x:DataType` em cada `DataTemplate`.
- Operações sobre arquivos do usuário devem ser **reversíveis** (journal) e nunca sobrescrever.

## Testes

- Mudou o parser, a sanitização, o formato ou a detecção de idioma de legendas? **Adicione casos em [`docs/test-cases.json`](docs/test-cases.json)** — eles rodam automaticamente (`NamingTests`) e servem de referência para ports.
- Fluxos com arquivos/TMDb: veja `WorkflowTests.cs` (usa `TempLibrary` + `FakeTmdb`, sem internet).
- Mudou uma regra de nomeação? Incremente `FolderFingerprint.RulesVersion` e atualize [docs/SPEC.md](docs/SPEC.md).

## Pull requests

1. Crie um branch a partir de `main`.
2. Garanta `dotnet build` sem avisos e `dotnet test` verde.
3. Atualize a documentação afetada (README, SPEC, BUILDING).
4. Descreva o problema, a solução e como testou; inclua capturas de tela para mudanças visuais.
