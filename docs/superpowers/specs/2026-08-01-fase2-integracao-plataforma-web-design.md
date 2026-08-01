# Fase 2 — Integração com a Plataforma Web (rating → nível)

## Contexto

Item da Fase 2 do `TODO.md`: "Construir a integração real com a plataforma web (rating →
nível)." Hoje a atribuição de nível na plataforma web (sistema externo, mesma equipe,
responsável pela formação de times) é 100% manual — nenhuma integração existe de fato,
apesar de `docs/integration-contract.md` já documentar um schema SQLite de leitura.

**Restrição decisiva de topologia:** o servidor CS2 (onde o plugin roda) e a plataforma
web rodam em máquinas diferentes. O servidor CS2 é hospedado pela FireGames, com pouca
liberdade de configuração no host. Isso descarta leitura direta do SQLite pela
plataforma web (exigiria filesystem compartilhado) — a plataforma não tem como alcançar
o arquivo. `PRAGMA journal_mode=WAL`, habilitado na Fase 1, permanece útil para
concorrência local do plugin, mas não resolve esse problema de topologia.

Este documento é escopo apenas deste repositório (o plugin). O lado receptor
(plataforma web) é responsabilidade de outro código/equipe; aqui definimos o contrato
que ela precisa implementar.

## Objetivo

O plugin deve enviar, de forma assíncrona e resiliente, o estado de rating/desempenho
de cada jogador para uma API da plataforma web, para que ela calcule o nível
automaticamente — substituindo a atribuição manual atual.

## Arquitetura

Sync outbound leve por **dirty-tracking + push em lote via timer em background**, não
um outbox clássico de eventos:

- Toda alteração em `players.rating` (fim de partida via `MatchService`, ou ação
  administrativa via `AdminCommands`: `SET`/`RESET`/`ADD`/`REMOVE`) marca o steamid
  como pendente de sync numa fila local. `WIPE` é tratado à parte (ver "Caso especial:
  WIPE" na seção Payload), já que apaga jogadores em vez de alterá-los.
- Um timer registrado no `MixRankingPlugin.Load()` (via `AddTimer` do
  CounterStrikeSharp, intervalo configurável) drena os pendentes periodicamente e envia
  um único `POST` em lote para a plataforma web, de forma assíncrona (não bloqueia a
  thread do jogo).
- Cada envio carrega o **estado atual** do jogador, não um delta/evento — isso torna o
  reenvio seguro por natureza (idempotente): se um lote falhar, ele é reenviado
  integralmente no próximo tick, sem necessidade de dedupe ou controle de
  sequência/ordem no lado da plataforma.
- A conexão é sempre de saída (outbound). O host da FireGames nunca precisa aceitar
  conexão de entrada — resolve a restrição de topologia sem exigir nada da hospedagem.

Deliberadamente mais simples que o "outbox" mencionado em `Arquitetura futura` do
`TODO.md` (que preserva histórico de eventos ordenados): como só o estado atual importa
para calcular o nível, não há necessidade de guardar/ordenar um log de eventos. YAGNI.

## Componentes

- **`RankingConfig`** — novos campos:
  - `WebSyncEnabled` (bool, default `false`) — sync desligado até ser configurado
    explicitamente; não quebra quem não usa.
  - `WebSyncUrl` (string) — endpoint HTTPS de destino.
  - `WebSyncApiKey` (string) — chave enviada no header `Authorization`.
  - `WebSyncIntervalSeconds` (int) — intervalo do timer de drenagem.
- **Migration 5 no `DatabaseService`** (bump `CurrentSchemaVersion` de 4 para 5): tabela
  `web_sync_queue` (upsert por `steamid` — estado atual, não log de eventos, portanto
  não cresce sem limite):
  - `steamid` (PK), `pending` (flag), e os campos do payload (ver abaixo).
  - **Não faz parte do contrato externo** (`integration-contract.md`) — é detalhe
    interno, já que ninguém mais lê o SQLite de fora após essa mudança.
- **`IWebSyncClient`** — interface que abstrai o `POST` HTTP, para permitir mock em
  teste sem chamada de rede real.
- **`WebSyncService`**:
  - `MarkDirtyAsync(steamid, ...campos do payload)` — upsert na fila.
  - `DrainPendingAsync()` — lê pendentes (lote limitado, ex: 100 linhas), monta o
    payload, chama `IWebSyncClient`, marca sucesso ou mantém pendente em falha.
- **Pontos de chamada de `MarkDirtyAsync`:**
  - `RatingService.ProcessMatchEndAsync`, logo após `WriteMatchEndResultAsync` persistir o
    novo rating de cada jogador (é `RatingService`, não `MatchService`, quem de fato
    grava no banco ao fim de partida).
  - `AdminCommands`, após `SET`/`RESET`/`ADD`/`REMOVE` (ações que alteram um jogador
    existente).
  - `WIPE` é um caso à parte — ver "Caso especial: WIPE" abaixo. Não usa
    `MarkDirtyAsync`, já que apaga os jogadores em vez de alterá-los.
- **`MixRankingPlugin.Load()`** — registra o timer chamando `DrainPendingAsync()` de
  forma fire-and-forget, apenas se `WebSyncEnabled` e `WebSyncUrl` for uma URL `https://`
  válida. Caso contrário, loga uma vez que o sync está desabilitado e segue o load
  normalmente (sync é aditivo, não crítico para o funcionamento do ranking).

## Payload

Campos por jogador, todos já existentes em `players` (nenhuma coleta nova):

```json
{
  "players": [
    {
      "steamid": "76561198...",
      "name": "player_name",
      "rating": 1234,
      "matches": 42,
      "wins": 25,
      "losses": 17,
      "kills": 512,
      "deaths": 430,
      "assists": 88,
      "damage": 98765,
      "mvps": 12,
      "created_at": "2026-01-15T10:00:00Z"
    }
  ]
}
```

Campos deliberadamente fora do payload:

- `updated_at` — redundante (a plataforma já sabe a hora do recebimento).
- Métricas derivadas (K/D, etc.) — a plataforma calcula a partir do que já recebe.
- `season_rating` — exigiria uma segunda consulta para resolver a temporada ativa;
  fora de escopo por ora, pode ser adicionado numa iteração futura sem redesenho.

### Caso especial: WIPE

`!rating_wipe` apaga todas as linhas de `players` (e das tabelas relacionadas) — não
existe mais estado de jogador para ler ao montar um upsert normal. Em vez de passar
por `web_sync_queue`, `WIPE` grava uma flag simples (`wipe_pending`, uma linha única de
estado, não por steamid). No próximo tick, `DrainPendingAsync` verifica essa flag
primeiro: se estiver marcada, envia uma requisição distinta —

```json
{ "wipe": true }
```

— para o mesmo endpoint, antes de processar a fila normal de upserts (que nesse ponto
deve estar vazia, já que `WIPE` limpa `web_sync_queue` também). A plataforma web deve
tratar `{"wipe": true}` como sinal para apagar todos os registros de rating/nível que
mantém, assim como `docs/integration-contract.md` já orienta para o cenário de leitura
direta do SQLite. A flag só é limpa após resposta `200`, seguindo a mesma semântica de
retry do restante do design.

## Segurança

- **Transporte:** HTTPS obrigatório. O plugin valida no `Load()` que `WebSyncUrl`
  começa com `https://`; caso contrário, trata como desabilitado (ver Componentes).
- **Autenticação:** header `Authorization: Bearer <WebSyncApiKey>`. Sem HMAC de
  payload — com TLS já garantindo confidencialidade e integridade em trânsito, uma
  assinatura adicional é complexidade sem ganho real aqui.
- **Armazenamento da chave:** vive no `RankingConfig` (JSON gerado em disco no host da
  FireGames), fora do controle de proteção deste projeto. O design mitiga o **blast
  radius**, não a exposição em si:
  - A chave autoriza apenas este endpoint de sync — nunca deve ser reaproveitada.
  - Fácil de rotacionar (a plataforma invalida/troca a chave; plugin só precisa de
    reload de config, sem novo deploy).
  - Nunca logada — nem em erro de autenticação (logs mostram status HTTP, nunca o
    header enviado).
  - Recomendação para o lado da plataforma (fora deste repositório): rate limiting no
    endpoint e sanity bounds no payload.
- **Replay:** como cada envio é o estado atual (não um evento), um replay tem impacto
  baixo — no máximo reaplica um valor antigo-mas-real até o próximo tick corrigir. Não
  justifica nonce/timestamp de proteção adicional agora.

## Contrato HTTP (para o time da plataforma implementar)

A documentar como nova seção em `docs/integration-contract.md`, mantendo a seção de
schema SQLite existente como está (não é mais o caminho ativo de integração, mas
permanece como referência do schema interno).

- **Endpoint:** `POST` configurável (`WebSyncUrl`), HTTPS obrigatório.
- **Auth:** header `Authorization: Bearer <WebSyncApiKey>`.
- **Request:** payload acima, em lote (array `players`).
- **Response esperada:** `200 OK` = lote inteiro aceito. Qualquer outro status = lote
  inteiro tratado como falha e reenviado no próximo tick. Sem semântica de aceitação
  parcial por item — all-or-nothing.
- **Idempotência garantida pelo remetente:** a plataforma pode sobrescrever pelo
  `steamid` sem deduplicação ou controle de sequência.
- **Timeout do lado do plugin:** curto (ex: 5s), para não acumular chamadas HTTP
  penduradas se a plataforma ficar lenta.

## Tratamento de erros

- **Falha de rede / timeout / status ≠ 200:** o lote inteiro permanece `pending = 1`
  em `web_sync_queue` e é reenviado no próximo tick — sem estado de erro especial, sem
  perda de dado (só atraso). Sem backoff exponencial — intervalo fixo do timer já
  resolve; complexidade de backoff só se necessidade real aparecer na prática.
- **Log:** falha loga via `ILogger` em `Warning` com status HTTP ou tipo de exceção
  (nunca o header de auth). Sucesso não precisa logar em `Information` a cada tick.
- **Exceção inesperada em `DrainPendingAsync`:** deve ser capturada ali mesmo — o
  callback do timer do CounterStrikeSharp não pode deixar uma exceção não tratada
  quebrar o timer ou o plugin. Ao contrário do fix recente de propagar erro de init do
  banco (que deve falhar alto), aqui o timer recorrente em background precisa engolir e
  logar.
- **`WebSyncEnabled = false` ou `WebSyncUrl` inválida/não-HTTPS:** plugin não registra o
  timer, loga uma vez no `Load()`, e segue carregando normalmente.

## Testes

Seguindo o padrão existente em `MixRanking.Tests`:

- **`WebSyncServiceTests`** — `MarkDirtyAsync` (upsert correto, inclusive sobrescrevendo
  um pendente anterior do mesmo steamid) e `DrainPendingAsync` (payload correto a partir
  das linhas pendentes, marca sucesso só quando `IWebSyncClient` retorna sucesso, mantém
  pendente em falha) usando um `IWebSyncClient` fake — sem chamada HTTP real.
- **Migration 5** (`web_sync_queue`) testada como as demais migrations existentes —
  criação de tabela, não comportamento.
- A implementação HTTP real (`HttpClient`) não é coberta por teste automatizado — fica
  para validação manual em staging, consistente com o débito técnico já registrado no
  `TODO.md` (ausência de projeto de teste automatizado era o estado antes da Fase 1;
  agora existe mas focado em lógica pura, não I/O externo).

## Fora de escopo

- Lado receptor (plataforma web) — outro código/equipe.
- `season_rating` no payload.
- Backoff exponencial / retry avançado.
- Aceitação parcial por item no lote.
- HMAC ou autenticação além de API key simples.
