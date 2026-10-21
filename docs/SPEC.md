# Especificação funcional (independente de linguagem)

Este documento descreve **o que** o MediaOrganizer faz, com precisão suficiente para reimplementá-lo em outra linguagem (veja também [PORTING.md](PORTING.md)). A implementação de referência está em `src/MediaOrganizer.Core`. Os casos de conformidade ficam em [`test-cases.json`](test-cases.json) e devem passar em qualquer port.

Convenções: "nome" sempre significa **nome do arquivo sem extensão**, salvo indicação. Comparações de nomes de arquivo ignoram maiúsculas/minúsculas (Windows e macOS não diferenciam), mas uma troca só de maiúsculas é considerada uma mudança.

---

## 1. Biblioteca e varredura

Uma **raiz** é uma pasta escolhida pelo usuário, do tipo `Series` ou `Movie`.

### 1.1 Séries
- Cada **subpasta direta** da raiz é uma série. Arquivos soltos na raiz são ignorados.
- Dentro da série, a varredura é recursiva. Os arquivos **ficam onde estão** (inclusive em subpastas de release); nada é movido e nenhuma pasta é renomeada.

### 1.2 Filmes
- Cada subpasta direta é um filme; se tiver mais de um vídeo, o filme é o **maior** (os demais ficam intactos).
- Cada vídeo solto na raiz também é um filme ("arquivo solto"); suas legendas são os arquivos de legenda na raiz cujo nome começa com o nome do vídeo.

### 1.3 Regras de varredura
- Ignorar entradas ocultas/de sistema e links simbólicos; ignorar pastas cujo nome começa com `.` ou `$`.
- Ignorar (em qualquer nível) pastas cujo nome esteja na lista configurável, sem diferenciar maiúsculas: `Extras, Featurettes, Behind The Scenes, Deleted Scenes, Interviews, Trailers, Bonus, AudioBooks, Sample, Samples`.
- **Pastas de especiais** — nome casa `^(specials?|season\s*0+|s0+)$` (sem diferenciar maiúsculas): são varridas se a opção `includeSpecials` estiver ligada (padrão) e ignoradas se estiver desligada, **independentemente** da lista acima (configurações da v1.0 ainda trazem `Specials` nela). Vídeos dentro delas, em qualquer nível, são marcados como *vídeos de especiais* (seção 2.6).
- Classificação de cada arquivo, nesta ordem:
  1. **Lixo** se o nome (com extensão) casar com algum padrão curinga (`*`, `?`, sem diferenciar maiúsculas) da lista: `*.nfo, *.url, *.website, *.exe, *rarbg*.txt, *psarips*.txt, www.*.txt, *torrent*.txt, *.torrent, sample*.mkv, sample*.mp4, *-sample.*, Thumbs.db`.
  2. **Vídeo**: `.mkv .mp4 .m4v .avi .mov .wmv .ts .m2ts .webm .mpg .mpeg .flv`.
  3. **Legenda**: `.srt .ass .ssa .sub .idx .vtt .sup`.
  4. Outros: ignorados.
- **Pasta vazia**: subpasta (não a da série) sem nenhum arquivo em toda a sua árvore. Pastas ignoradas nunca contam como vazias.
- Uma série/filme só é listada se tiver ao menos um vídeo ou um lixo.

---

## 2. Interpretação de nomes de download

### 2.1 Normalização de separadores
1. Trocar todo `.` por espaço, **exceto** pontos entre dois dígitos (`1.23.45` é preservado). Regex: `(?<!\d)\.|\.(?!\d)` → `" "`.
2. Trocar `_` por espaço.
3. Colapsar espaços repetidos e aparar.

### 2.2 Marcador de episódio (primeiro que casar, sem diferenciar maiúsculas)
| Ordem | Regex | Exemplo | Temporada |
| --- | --- | --- | --- |
| 1 | `\bS(?<s>\d{1,2})\s?E(?<e>\d{1,3})(?:\s?-?\s?E(?<e2>\d{1,3}))?\b` | `S01E01`, `S04E01-E02`, `S01E01E02` | `s` |
| 2 | `\b(?<s>\d{1,2})x(?<e>\d{2,3})\b` | `2x05` | `s` |
| 3 | `\b(?:E|Ep)\s?(?<e>\d{1,3})\b` | `E01`, `Ep 3` | **1** |

- `e2` só vale se `e2 > e` (senão é ignorado).
- Sem marcador → o arquivo não é um episódio (fica com status *Ignorado*).

### 2.3 Nome da série e ano
- Texto **antes** do marcador, aparado de ` - – _ . [ ( ,`.
- Se terminar com um ano `(19|20)\d\d` (opcionalmente entre parênteses/colchetes) e o ano **não** for o texto inteiro, o ano é separado: `Dark Matter 2024` → (`Dark Matter`, 2024). O ano é só uma dica de busca.

### 2.4 Título do episódio no arquivo
- Texto **depois** do marcador, cortado na primeira **tag técnica** e aparado de ` - – _ . [ ( ,`. Vazio → sem título.
- Tags técnicas (regex, sem diferenciar maiúsculas, com `\b` dos dois lados e `(` ou `[` opcional antes):

```
2160p|1440p|1080p|1080i|720p|576p|480p|4k|uhd|10bit|8bit|hdr10\+?|hdr|dv|dovi|sdr|
web-?rip|web-?dl|blu-?ray|bdrip|brrip|bdremux|remux|hdtv|dvdrip|dvd|hdrip|
x264|x265|h ?264|h ?265|hevc|avc|xvid|av1|
aac\d?|ac3|eac3|ddp?\d?|dd\+|dts(?:-hd)?|truehd|atmos|flac|opus|mp3|\dch|
repack\d?|proper|internal|dublado|legendado|
amzn|nf|dsnp|hmax|atvp|pcok|pmtp|
extended|unrated|remastered|imax          ← palavras de edição
```

### 2.5 Filmes
1. Normalizar (2.1) e cortar na primeira tag (2.4).
2. Procurar anos `[\(\[]?\b(19|20)\d\d\b[\)\]]?` que **não** estejam na posição 0; o **último** é o ano do filme e o título é o texto antes dele (aparado). Sem ano → título = texto inteiro.
   - `2001 A Space Odyssey 1968` → (`2001 A Space Odyssey`, 1968); `Blade Runner 2049 (2017)` → (`Blade Runner 2049`, 2017).

### 2.6 Episódios especiais (temporada 0)
- **Com número** (`S00E01`, em qualquer pasta): seguem as regras normais com temporada 0; os títulos vêm de `tv/{id}/season/0`.
- **Sem número**, apenas para *vídeos de especiais* (1.3):
  1. Texto útil: normalizar (2.1), cortar na primeira tag técnica **sem** as palavras de edição (`Pilot Extended` mantém `Extended`) e remover do início o nome da série — o primeiro, do mais longo para o mais curto, entre: nome da pasta, título preferido, título em inglês e título original. Vazio → sem sugestão.
  2. Normalizar o texto e os títulos da temporada 0 (no idioma da obra e em inglês; títulos genéricos são descartados) com a função de 3.2. Texto normalizado com menos de 4 caracteres → sem sugestão.
  3. Igualdade vence; senão, o título mais longo (≥ 4 caracteres) contido no texto ou que contenha o texto.
  4. Encontrado: destino `Série S00Exx - <título oficial>` com status *Aviso* ("Especial identificado pelo título; confira antes de aplicar.") e `needsConfirmation = true` — o item começa **desmarcado**. Editar o nome remove a marca. Não encontrado: *Ignorado*.
- Casos de conformidade: seção `specialTitles` de `test-cases.json`.

---

## 3. Identificação no TMDb

API v3 `https://api.themoviedb.org/3/`. Autenticação: se a chave começa com `eyJ` (token v4/JWT) → cabeçalho `Authorization: Bearer <chave>`; senão → parâmetro `api_key=<chave>`.

| Uso | Endpoint |
| --- | --- |
| Buscar série | `GET search/tv?query=&first_air_date_year=&language=` |
| Buscar filme | `GET search/movie?query=&year=&language=` |
| Detalhes da série | `GET tv/{id}?language=en-US` |
| Temporada | `GET tv/{id}/season/{n}?language=` |
| Detalhes do filme | `GET movie/{id}?language=en-US` |
| Validar chave | `GET configuration` |

Respostas `404` = não encontrado; `401` = chave inválida (erro global). As respostas são cacheadas por 7 dias (chave = caminho + query **sem** a chave). No máximo 8 requisições simultâneas; `429`/falhas transitórias usam retentativa com backoff.

### 3.1 Consultas
- **Séries**: `[nome da pasta limpo (2.5), nome de série mais comum nos arquivos (2.3)]`, ano = ano da pasta ou o mais comum nos arquivos.
- **Filmes**: `[título do nome da pasta, título do nome do arquivo]`, ano = da pasta ou do arquivo.
- Para cada idioma em `[en-US, pt-BR]` (para no primeiro que tiver correspondência exata) e cada consulta: buscar com o ano; se vazio e havia ano, buscar sem ano. Avaliar os 10 primeiros resultados.

### 3.2 Pontuação
`normalizar(x)` = trocar `&` por ` and `, decompor Unicode (NFD) e remover marcas de acento, manter letras/dígitos em minúsculas e trocar o resto por espaço, colapsar espaços.

```
pontos = 100 se normalizar(consulta) == normalizar(título) ou normalizar(título original)
       |  40 se um começa com o outro
       |   0 caso contrário
se há ano e o candidato tem ano: +50 se igual, +20 se diferença ≤ 1, −30 caso contrário
+ min(10, log10(1 + popularidade) × 4)   (inteiro, desempate)
```

O maior vence. **Baixa confiança** se `pontos < 100` ou se o ano do candidato difere do esperado em mais de 1. Depois, os detalhes são lidos em `en-US` (para ter o nome canônico mesmo quando a busca veio em pt-BR).

### 3.3 Correspondência memorizada
Escolhas são salvas por chave `"<Kind>/<nome da pasta>"` (ex.: `Series/Dark`). Uma correspondência é salva automaticamente quando a confiança é alta, quando o usuário escolhe/confirma, ou ao aplicar.

---

## 4. Idioma dos títulos

- **Idiomas de alfabeto latino** (ISO 639-1): `af bs ca cs cy da de en eo es et eu fi fo fr ga gl hr hu id is it la lb lt lv ms mt nb nl nn no pl pt ro sk sl sq sv sw tl tr vi`.
- Nome da série: `original_name` se o idioma original for latino; senão `name` em inglês.
- Episódios: idioma = idioma original se latino e diferente de `en`; senão `en-US`.
- **Título genérico** (vazio ou casa `^(episode|episódio|episodio|épisode|folge|aflevering|afl\.?|odcinek|avsnitt|jakso|bölüm|puntata|capítulo|capitulo|epizoda|epizód|afsnit|episodi)\s*\d+$`, sem diferenciar maiúsculas): buscar o mesmo episódio em `en-US`; se também for genérico, manter o original.
- Episódio inexistente no TMDb → usar o título do arquivo (2.4), se houver, com aviso.

---

## 5. Formato dos nomes

### 5.1 Sanitização (aplicada a títulos de série, episódio e filme)
Em ordem:
1. `:` entre dígitos → `.` (`1:23:45` → `1.23.45`).
2. `\s*:\s+` → ` - ` (`Monster: The Ed Gein Story` → `Monster - The Ed Gein Story`).
3. `:` restante → `-`; `/` e `\` → `-`; `"` → `'`.
4. Remover `< > | ? *` e caracteres de controle `\x00-\x1F`.
5. Colapsar espaços, aparar, remover `.` e espaços do **final**.
6. Nomes reservados do Windows (`CON PRN AUX NUL COM1-9 LPT1-9`) recebem `_` no fim.

### 5.2 Episódio
```
<Série> S<tt>E<ee>[-E<ff>][ - <Título>].<extensão original>
```
`tt/ee/ff` com 2 dígitos. Em arquivos com vários episódios, os títulos distintos são unidos por ` & `. Sem título → sem o ` - `.

### 5.3 Filme
```
<Título> (<Ano>)/<Título> (<Ano>).<extensão original>
```
Sem ano → só `<Título>`. Arquivo solto: o destino é uma nova pasta na raiz. Filme em pasta: o vídeo é renomeado dentro da pasta e, **por último**, a pasta é renomeada.

### 5.4 Legendas
Novo nome: `<novo nome base do vídeo>[.<idioma>][.<flag>].<extensão original>`, **na mesma pasta onde a legenda já está**. Se o nome repetir, acrescentar `.2`, `.3`… antes da extensão.

Encontrar o vídeo dono (primeira regra que funcionar):
1. Mesma pasta e o nome da legenda começa com o nome do vídeo (o mais longo vence). Trecho analisado = o que sobra depois do nome do vídeo.
2. Padrão RARBG: legenda em `<pasta do vídeo>/Subs/<nome do vídeo>/…`. Trecho = nome da legenda (ex.: `3_English`).
3. Legenda em pasta `Sub`, `Subs`, `Subtitle(s)` ou `Legenda(s)` e **um único** vídeo na pasta acima. Trecho = nome da legenda.
4. Mesmo `SxxExx` (2.2) que um vídeo da mesma pasta ou da pasta acima. Trecho = os 3 últimos tokens do nome.
5. Nenhum → legenda ignorada.

Idioma/flag do trecho: se casar `pt[\s._-]?br|\bpob\b|brazil` → `pt-BR`. Quebrar em tokens por `[\s._\-()\[\]]+`; `forced` → flag `forced`; `sdh|cc|hi` → flag `sdh`; o primeiro token da tabela de idiomas (nomes e códigos ISO 639-1/639-2, ex.: `English/eng/en → en`, `Portuguese/por/pt → pt`, `Spanish/spa/es → es`) define o idioma. Tabela completa em `SubtitleMatcher.cs`.

---

## 6. Plano, status e conflitos

Cada item: `{tipo: Video|Subtitle|Folder, origem, destino, status, mensagem, needsConfirmation}`. Itens com `needsConfirmation` começam desmarcados na prévia.

| Status | Significado | Aplicável |
| --- | --- | --- |
| `Ok` | será renomeado | sim |
| `Warning` | será renomeado com ressalva (título não encontrado) | sim |
| `Unchanged` | origem == destino (comparação exata) | — |
| `Skipped` | não foi possível determinar o destino | — |
| `Conflict` | bloqueado | não |

**Conflitos** (considerando só itens aplicáveis):
- dois itens com o mesmo destino (sem diferenciar maiúsculas) → todos em conflito;
- o destino já existe em disco e **não** é o próprio arquivo com outra caixa → conflito;
- nome de destino vazio → conflito.

Edições do usuário recalculam o status do item e os conflitos da pasta.

**Status da pasta**: `NeedsReview` se baixa confiança ou algum conflito; senão `Changes` se há algo aplicável; senão `NoChanges`. Também: `AlreadyProcessed` (seção 8), `NotFound`, `Error`. Pastas com baixa confiança não são aplicadas até o usuário confirmar.

---

## 7. Aplicação e desfazer (journal)

1. Ordenar: arquivos primeiro, pastas por último.
2. **Antes** de mover qualquer coisa, gravar o journal em `data/history/<id>.json` com todas as operações `Pending`. `id = yyyyMMdd-HHmmss-<6 hex>`.
3. Para cada operação: criar a pasta de destino se preciso; mover **sem sobrescrever**. Troca só de maiúsculas: mover para `<destino>.mo-tmp` e depois para o destino. Marcar `Done` ou `Failed` (+ mensagem). Regravar o journal a cada 25 operações e no fim.
4. **Desfazer**: percorrer as operações `Done` em ordem inversa movendo `to → from`; marcar `Undone`; se a pasta de onde o arquivo saiu ficou vazia e não é a pasta de destino original, removê-la (pasta criada para filme solto). Gravar `undoneAt`.

Formato (camelCase, enums como texto):
```json
{
  "id": "20261019-235101-a1b2c3",
  "createdAt": "2026-10-19T23:51:01-03:00",
  "libraryPath": "E:\\Series",
  "kind": "Series",
  "description": "Dark: 4 arquivo(s)",
  "folderKeys": ["Series/Series/Dark"],
  "operations": [
    { "from": "E:\\Series\\Dark\\Dark.S01E01.mkv", "to": "E:\\Series\\Dark\\Dark S01E01 - Geheimnisse.mkv",
      "isDirectory": false, "state": "Done", "error": null }
  ],
  "undoneAt": null
}
```

Lixo é enviado à Lixeira do sistema (Windows: Shell `SHFileOperation` com `FOF_ALLOWUNDO`; macOS: mover para `~/.Trash`; Linux: especificação freedesktop.org — `~/.local/share/Trash/files` + `info/*.trashinfo`). Nunca é apagado definitivamente.

---

## 8. Fingerprint ("nada foi alterado")

**Chave da pasta**: `"<Kind>/<nome da pasta raiz>/<nome da pasta>"` (ex.: `Series/Series/Dark`) — não usa caminho absoluto, para o mesmo HD funcionar em `E:\Series`, `/Volumes/HD/Series` ou `/mnt/hd/Series`.

**Hash**: XxHash128 (saída em hex minúsculo) sobre os bytes UTF-8 de:
```
"v<RulesVersion>|tmdb:<id>\n"
+ para cada arquivo (vídeos + legendas), ordenado por comparação ordinal:
  "<caminho relativo à pasta, com '/'>|<tamanho em bytes>\n"
```
`RulesVersion` = 2 (v1.0 = 1; v1.1 = 2 por causa dos especiais — incrementar sempre que as regras de nomeação mudarem). A data de modificação **não** entra (precisão diferente entre NTFS/exFAT/APFS/ext4). Para filmes soltos o caminho é relativo à raiz.

**Quando gravar**: (a) ao analisar uma pasta que já está toda no padrão (`NoChanges`) com correspondência confiável; (b) após aplicar, se **todas** as mudanças da pasta foram aplicadas com sucesso — recalculado a partir da pasta reescaneada (no caso de filme, já com o novo nome).

**Na análise**: se não for "forçar", existe correspondência memorizada, o registro tem o mesmo id do TMDb e o hash atual é igual → status `AlreadyProcessed` com a mensagem *"Nada foi alterado desde o último processamento."*, **sem** consultar o TMDb. Se todas as pastas estiverem assim, a interface mostra um aviso geral. Desfazer remove os registros das pastas afetadas.

`data/processed.json`:
```json
{
  "version": 1,
  "folders": {
    "Series/Series/Dark": { "hash": "9f1c…", "tmdbId": 70523, "processedAt": "2026-10-19T23:51:02-03:00" }
  }
}
```

---

## 8A. Coleção (episódios faltando)

Somente para séries. Usa a mesma varredura (1.1–1.3) e as **correspondências memorizadas** (3.3); não faz busca automática.

1. Pasta sem correspondência memorizada → status `NoMatch`, sem nenhuma chamada ao TMDb. Erro de rede → `Error`.
2. Detalhes: `tv/{id}?language=en-US`. Temporadas = `seasons[].season_number` (ou `1..number_of_seasons` se ausente) ∪ temporadas presentes nos arquivos. A temporada 0 só entra com `includeSpecials` ligado.
3. Episódios de cada temporada: `tv/{id}/season/{n}` no idioma da seção 4; títulos com o mesmo fallback para inglês.
4. **Presentes**: `SxxExx` dos vídeos (2.2); `S04E01-E02` conta os dois.
5. **Todos** os episódios oficiais da temporada são listados, na ordem do TMDb, cada um com uma situação. **Exibido** = `air_date` (formato `yyyy-MM-dd`) ≤ hoje (data local):

   | Exibido | Na pasta | Situação | Conta no progresso |
   | --- | --- | --- | --- |
   | sim | sim | `Present` — "Na pasta" | presente e esperado |
   | sim | não | `Missing` — "Faltando" | esperado |
   | não (futuro ou sem data) | não | `Upcoming` — "Não exibido" | não |
   | não (futuro ou sem data) | sim | `PresentNotAired` — "Na pasta (ainda não exibido)" | não |

   Arquivos de episódios que o TMDb não lista são desconsiderados.
6. Temporadas sem nenhum episódio oficial são omitidas. Na interface, a primeira temporada regular começa expandida e as demais (e os especiais) recolhidas.
7. Status da série: `Incomplete` se alguma temporada **regular** (≠ 0) tiver faltando; senão `Complete`. Os especiais aparecem à parte e não entram no progresso (`presentes / exibidos` das temporadas regulares).
8. "Atualizar" refaz as chamadas ignorando o cache (a resposta nova substitui a cacheada).

**Exportação** (somente faltando das temporadas regulares, séries em ordem alfabética):
- CSV com `;`, UTF-8 com BOM, cabeçalho `Série;Temporada;Episódio;Título;Exibido em`; data `yyyy-MM-dd`; campos com `;`, `"` ou quebra de linha entre aspas (aspas duplicadas).
- Texto: por série, `"<Série> — faltam N episódio(s)"` seguido de linhas `"  SxxEyy  Título  (dd/MM/yyyy)"`.

---

## 9. Dados do aplicativo

Pasta `data/`, resolvida nesta ordem:
1. variável de ambiente `MEDIAORGANIZER_DATA`;
2. em desenvolvimento, a pasta que contém `MediaOrganizer.slnx` (procurando a partir do executável para cima);
3. no macOS, se o executável está em `X.app/Contents/MacOS`, a pasta que contém o `.app`;
4. a pasta do executável;
5. se a escolhida não for gravável: pasta de dados do usuário (`%AppData%`, `~/Library/Application Support`, `~/.config`) + `/MediaOrganizer`.

`settings.json`:
```json
{
  "tmdbApiKey": "…",
  "lastLibraryPath": "E:\\Series",
  "lastKind": "Series",
  "renameSubtitles": true,
  "includeSpecials": true,
  "ignoredFolders": ["Extras", "Featurettes", "…"],
  "junkPatterns": ["*.nfo", "…"],
  "matches": { "Series/Dark": 70523, "Series/Round 6": 93405 }
}
```
Toda gravação é atômica (escreve `arquivo.tmp` e substitui).
