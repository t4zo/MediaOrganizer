# Roadmap

Planejamento das próximas versões do MediaOrganizer. As prioridades podem mudar conforme o uso real; sugestões são bem-vindas via *issues*.

Legenda: ✅ concluído · 🚧 em andamento · 📋 planejado · 💡 ideia (a avaliar)

## v1.0 — Renomeação padronizada ✅

- ✅ Renomear episódios para `Série SXXEXX - Título.ext` com títulos oficiais do TMDb.
- ✅ Títulos no idioma original (alfabeto latino) ou inglês; nunca traduções.
- ✅ Prévia editável, conflitos bloqueados, desfazer por lote (Histórico).
- ✅ Renomear legendas junto (inclui `Subs/<release>/N_Language.srt`).
- ✅ Filmes: `Filme (Ano)/Filme (Ano).ext`.
- ✅ Fingerprint por pasta: "nada foi alterado desde o último processamento".
- ✅ Lixo de release e pastas vazias para a Lixeira.
- ✅ Executável único por sistema (Windows, macOS arm64/x64, Linux x64) e dados portáteis em `data/`.

## v1.1 — Distribuição automática 📋

**Publicação de versões no GitHub (CI/CD)**

- Workflow do GitHub Actions que, a cada push/PR, compila e roda os testes em Windows, macOS e Linux.
- Ao criar uma tag `vX.Y.Z`, gerar os executáveis com `build.sh`/`build.ps1` e publicá-los automaticamente em **Releases** (com `.zip` do `.app` do macOS para preservar permissões).
- Changelog gerado a partir dos commits/PRs.
- Opcional: assinatura de código (Authenticode no Windows; Developer ID + notarização no macOS) para eliminar os avisos de segurança na primeira execução.

Critério de pronto: criar a tag `v1.1.0` publica os 4 executáveis sem passos manuais.

## v1.2 — Episódios especiais (temporada 0) 📋

- Reconhecer especiais nos nomes (`S00E01`, `Special`, `OVA`) e na pasta `Specials` (hoje ignorada por padrão).
- Buscar títulos em `tv/{id}/season/0` no TMDb e nomear como `Série S00E01 - Título.ext`.
- Correspondência por título quando o arquivo não tiver número (ex.: "Breaking Bad - Pilot Extended"), com confirmação na prévia.
- Configuração para escolher: ignorar especiais (comportamento atual) ou incluí-los.

Critério de pronto: uma pasta `Specials` com arquivos de download é renomeada com os títulos oficiais da temporada 0.

## v1.3 — Detectar episódios faltando 📋

- Comparar os episódios presentes em cada série com a lista oficial do TMDb (temporadas e episódios já exibidos).
- Nova tela **Coleção**: por série, temporadas completas/incompletas, episódios faltando e episódios ainda não lançados (com data de estreia).
- Filtro "só séries incompletas" e exportação da lista (texto/CSV).
- Reaproveita o fingerprint: pastas sem alterações usam os dados em cache.

Critério de pronto: a tela lista corretamente os episódios ausentes de uma série com temporadas parciais.

## v1.4 — Baixar legendas 📋

- Buscar legendas para episódios/filmes sem legenda no idioma desejado (ex.: `pt-BR`, `en`).
- Provedor inicial: [OpenSubtitles](https://opensubtitles.stoplight.io/) (API com chave gratuita); busca por *hash* do arquivo e, em seguida, por título/temporada/episódio.
- Salvar ao lado do vídeo já no padrão (`Série S01E01 - Título.pt-BR.srt`), registrando no Histórico para poder desfazer.
- Configuração de idiomas preferidos e opção de incluir legendas forçadas/SDH.

Critério de pronto: selecionar uma série e baixar as legendas `pt-BR` faltantes em um clique, com prévia antes de salvar.

## Ideias a avaliar 💡

- Modo linha de comando (CLI) reutilizando o Core, para agendar a organização.
- Monitorar pastas e organizar automaticamente arquivos novos (com notificação).
- Gerar arquivos `.nfo` e baixar pôsteres para Kodi/Jellyfin/Plex.
- Fonte alternativa de metadados (TVmaze) como fallback.
- Interface em inglês (internacionalização).
