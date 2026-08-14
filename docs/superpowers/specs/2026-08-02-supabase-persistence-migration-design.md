# Migração da persistência: SQLite local → Supabase dedicado

## Contexto

O host do servidor CS2 reverte a pasta do plugin (`addons/counterstrikesharp/plugins/MixRanking/`, incluindo `mixranking.db`) para um estado antigo a cada restart. Confirmado via `admin_audit_log` de um `mixranking.db` baixado do host: só existe **um** registro de `WIPE`, de um horário muito anterior a duas partidas que sabidamente foram gravadas com sucesso (log do plugin + payload recebido pela plataforma web) e que, ainda assim, não existem mais no arquivo baixado depois. Ou seja, dados são persistidos corretamente durante a sessão, mas o arquivo local não sobrevive a um restart do servidor.

Não há liberdade de configuração nesse host (sem SSH, sem acesso a scripts de startup) para investigar ou desabilitar esse comportamento — decisão do projeto: **parar de depender de qualquer armazenamento no próprio host do servidor de jogo**, e passar a depender de um banco Postgres hospedado (Supabase), dedicado a este plugin e separado do Supabase que já existe para a plataforma web.

Este documento é escopo apenas deste repositório (o plugin CS2). O provisionamento do projeto Supabase (aplicar o schema abaixo, gerenciar a chave de API) é responsabilidade de quem mantém esse lado — aqui definimos o contrato exato que o plugin espera.

## Objetivo

Substituir o `DatabaseService` local (SQLite, `Microsoft.Data.Sqlite`) por uma implementação que persiste tudo — jogadores, partidas, histórico de rating, temporadas, audit log de admin, fila de sync com a plataforma web — num projeto Supabase dedicado, via API REST (PostgREST), sem alterar o comportamento observável do plugin (mesmos comandos, mesmas regras de negócio, mesma UX de chat).

## Restrição decisiva de topologia

Hosts de servidor de jogo tipicamente liberam apenas saída HTTP/HTTPS (portas 80/443) no firewall, bloqueando conexão direta em porta de banco (5432 do Postgres). O sync outbound existente (`HttpWebSyncClient`, ver `docs/integration-contract.md` seção 4) já prova que conexão de saída HTTPS funciona nesse host. Por isso o plugin fala com o Supabase via **API REST do PostgREST**, nunca via connection string Postgres direta — mesmo padrão de rede que já é comprovadamente compatível com o ambiente de produção.

## Arquitetura

- **`IDatabaseService`** — nova interface com os 23 métodos hoje públicos em `DatabaseService` (assinaturas inalteradas). `RatingService`, `PlayerService`, `WebSyncService` e `AdminCommands` passam a depender da interface, não da classe concreta.
- **`SupabaseDatabaseService : IDatabaseService`** — implementação de produção. Usa `HttpClient` contra a REST API do PostgREST (`{SupabaseUrl}/rest/v1/...`) para leituras/escritas simples, e `POST {SupabaseUrl}/rest/v1/rpc/<função>` para as operações que hoje são uma transação SQLite multi-tabela (ver "Funções RPC" abaixo).
- **`FakeDatabaseService : IDatabaseService`** — fake em memória só para teste, no mesmo espírito de `FakeWebSyncClient` (já existe em `WebSyncServiceTests.cs`).
- **Removido:** `DatabaseService` (SQLite), todo o sistema de migrations (`Migration1_Baseline` … `Migration5_WebSyncQueue`, `PRAGMA user_version`), `Microsoft.Data.Sqlite` como dependência do projeto principal (pode continuar como dependência só do projeto de teste, se necessário).
- **Inalterado:** `WebSyncService` / `HttpWebSyncClient` / `IWebSyncClient` (sync outbound para a plataforma web) continuam existindo exatamente como estão — agora fazem dirty-tracking sobre dados que moram no Supabase em vez de SQLite local, mas a lógica deles não muda nem um pouco.

## Schema Postgres (projeto Supabase novo)

Tradução direta do schema SQLite atual (documentado em `docs/integration-contract.md` seção 2) para Postgres. Ponto de partida — ajustar conforme necessário ao aplicar:

```sql
CREATE TABLE players (
    steamid TEXT PRIMARY KEY,
    name TEXT NOT NULL DEFAULT '',
    rating INTEGER NOT NULL DEFAULT 1000,
    matches INTEGER NOT NULL DEFAULT 0,
    wins INTEGER NOT NULL DEFAULT 0,
    losses INTEGER NOT NULL DEFAULT 0,
    kills INTEGER NOT NULL DEFAULT 0,
    deaths INTEGER NOT NULL DEFAULT 0,
    assists INTEGER NOT NULL DEFAULT 0,
    damage BIGINT NOT NULL DEFAULT 0,
    mvps INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE seasons (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name TEXT NOT NULL,
    started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at TIMESTAMPTZ,
    is_active BOOLEAN NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX idx_seasons_single_active ON seasons(is_active) WHERE is_active = true;
INSERT INTO seasons (name, is_active) VALUES ('Season 1', true);

CREATE TABLE season_ratings (
    season_id BIGINT NOT NULL REFERENCES seasons(id),
    steamid TEXT NOT NULL REFERENCES players(steamid),
    rating INTEGER NOT NULL DEFAULT 1000,
    matches INTEGER NOT NULL DEFAULT 0,
    wins INTEGER NOT NULL DEFAULT 0,
    losses INTEGER NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (season_id, steamid)
);

CREATE TABLE matches (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    match_guid TEXT NOT NULL,
    map TEXT NOT NULL DEFAULT '',
    winner_team INTEGER NOT NULL DEFAULT 0,
    ct_score INTEGER NOT NULL DEFAULT 0,
    t_score INTEGER NOT NULL DEFAULT 0,
    finished_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    season_id BIGINT NOT NULL REFERENCES seasons(id)
);
CREATE INDEX idx_matches_guid ON matches(match_guid);

CREATE TABLE rating_history (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    match_id BIGINT NOT NULL REFERENCES matches(id),
    steamid TEXT NOT NULL REFERENCES players(steamid),
    old_rating INTEGER NOT NULL,
    base_change INTEGER NOT NULL,
    performance_swing INTEGER NOT NULL,
    total_change INTEGER NOT NULL,
    new_rating INTEGER NOT NULL,
    season_id BIGINT NOT NULL REFERENCES seasons(id),
    k_factor_used INTEGER
);
CREATE INDEX idx_rating_history_steamid ON rating_history(steamid);
CREATE INDEX idx_rating_history_match ON rating_history(match_id);

CREATE TABLE match_player_stats (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    match_id BIGINT NOT NULL REFERENCES matches(id),
    steamid TEXT NOT NULL REFERENCES players(steamid),
    team INTEGER NOT NULL,
    kills INTEGER NOT NULL DEFAULT 0,
    deaths INTEGER NOT NULL DEFAULT 0,
    assists INTEGER NOT NULL DEFAULT 0,
    damage INTEGER NOT NULL DEFAULT 0,
    rounds_played INTEGER NOT NULL DEFAULT 0,
    rounds_survived INTEGER NOT NULL DEFAULT 0,
    rounds_with_kill INTEGER NOT NULL DEFAULT 0,
    mvps INTEGER NOT NULL DEFAULT 0,
    opening_kills INTEGER NOT NULL DEFAULT 0,
    opening_deaths INTEGER NOT NULL DEFAULT 0,
    trade_kills INTEGER NOT NULL DEFAULT 0,
    flash_assists INTEGER NOT NULL DEFAULT 0,
    clutches_won INTEGER NOT NULL DEFAULT 0,
    headshots INTEGER NOT NULL DEFAULT 0,
    hs_percent DOUBLE PRECISION NOT NULL DEFAULT 0,
    adr DOUBLE PRECISION NOT NULL DEFAULT 0,
    kd_ratio DOUBLE PRECISION NOT NULL DEFAULT 0,
    kast_percent DOUBLE PRECISION NOT NULL DEFAULT 0,
    abandoned BOOLEAN NOT NULL DEFAULT false,
    season_id BIGINT NOT NULL REFERENCES seasons(id)
);
CREATE INDEX idx_match_player_stats_steamid ON match_player_stats(steamid);
CREATE INDEX idx_match_player_stats_match ON match_player_stats(match_id);
CREATE UNIQUE INDEX idx_match_player_stats_match_steamid ON match_player_stats(match_id, steamid);

CREATE TABLE admin_audit_log (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    action TEXT NOT NULL,
    admin_steamid TEXT,
    admin_name TEXT NOT NULL DEFAULT 'CONSOLE',
    target_steamid TEXT,
    old_value INTEGER,
    new_value INTEGER,
    reason TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_admin_audit_log_target ON admin_audit_log(target_steamid);
CREATE INDEX idx_admin_audit_log_created ON admin_audit_log(created_at);

CREATE TABLE web_sync_queue (
    steamid TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    rating INTEGER NOT NULL,
    matches INTEGER NOT NULL,
    wins INTEGER NOT NULL,
    losses INTEGER NOT NULL,
    kills INTEGER NOT NULL,
    deaths INTEGER NOT NULL,
    assists INTEGER NOT NULL,
    damage BIGINT NOT NULL,
    mvps INTEGER NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE web_sync_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    wipe_pending BOOLEAN NOT NULL DEFAULT false
);
INSERT INTO web_sync_state (id, wipe_pending) VALUES (1, false);
```

Recomendação: manter Row Level Security (RLS) **desligado** nessas tabelas (ou uma policy permissiva só para a `service_role`), já que o único cliente é o próprio plugin autenticado com a `service_role key` — não há usuários finais acessando essas tabelas diretamente.

## Funções RPC

PostgREST não oferece transação client-side entre chamadas REST separadas, nem agregação (`SUM`) via filtro REST simples. Duas categorias de método viram funções `plpgsql` chamadas via `POST /rest/v1/rpc/<nome>`:

**Leitura agregada (1 função):**

- **`get_player_total_rounds(p_steamid text) RETURNS int`** — `SELECT COALESCE(SUM(rounds_played), 0) FROM match_player_stats WHERE steamid = p_steamid`. Espelha `GetPlayerTotalRoundsPlayedAsync` (`Database/DatabaseService.cs:429`). Só leitura, sem necessidade de atomicidade — RPC aqui é só porque PostgREST não expõe `SUM` num GET simples.

**Escritas atômicas (5 funções):** operações que hoje são uma transação SQLite multi-tabela, cada uma atômica por natureza (uma função Postgres roda dentro de uma transação implícita).

1. **`write_match_end_result(match jsonb, updates jsonb, p_initial_rating int, p_active_season_id bigint) RETURNS bigint`**
   Insere em `matches`, e para cada item de `updates` (array JSON com steamid, nome, rating change, stats detalhados — ver payload de exemplo abaixo): upsert em `players` (cria se não existir), `UPDATE players SET matches = matches + 1, ...`, `INSERT INTO rating_history`, `INSERT INTO match_player_stats`, upsert em `season_ratings`. Espelha exatamente `WriteMatchEndResultAsync` (`Database/DatabaseService.cs:601-795`). Retorna o `id` da partida inserida.

   Payload de `updates` (um item do array), campos vêm de `MatchPlayerUpdate` + `MatchPlayerStats`:
   ```json
   {
     "steamid": "76561198...",
     "player_name": "Manu",
     "old_rating": 1000, "base_change": 30, "performance_swing": 5,
     "total_change": 35, "new_rating": 1035, "k_factor_used": 100, "won": true,
     "team": 3, "kills": 20, "deaths": 15, "assists": 2, "damage": 2103,
     "rounds_played": 22, "rounds_survived": 10, "rounds_with_kill": 15,
     "mvps": 2, "opening_kills": 1, "opening_deaths": 0, "trade_kills": 3,
     "flash_assists": 1, "adr": 95.6, "kd_ratio": 1.33, "kast_percent": 70.5,
     "abandoned": false
   }
   ```

2. **`set_player_rating(p_target_steamid text, p_new_rating int, p_admin_steamid text, p_admin_name text, p_reason text) RETURNS void`** — espelha `SetPlayerRatingWithAuditAsync` (linha 797).
3. **`reset_player(p_target_steamid text, p_initial_rating int, p_admin_steamid text, p_admin_name text, p_reason text) RETURNS void`** — espelha `ResetPlayerWithAuditAsync` (linha 845).
4. **`adjust_player_rating(p_target_steamid text, p_amount int, p_is_add boolean, p_min_rating int, p_admin_steamid text, p_admin_name text, p_reason text) RETURNS void`** — espelha `AdjustPlayerRatingWithAuditAsync` (linha 898). Todas as três lançam exceção (`RAISE EXCEPTION`) se o steamid não existir em `players`, igual ao `throw new Exception("Jogador não encontrado.")` atual — o PostgREST traduz isso automaticamente pra um HTTP 400 com a mensagem no corpo.
5. **`reset_all_data(p_initial_rating int, p_admin_steamid text, p_admin_name text, p_reason text) RETURNS void`** — espelha `ResetAllDataWithAuditAsync` (linha 949): insere audit log com `action = 'WIPE'`, depois `DELETE FROM` em `rating_history`, `match_player_stats`, `season_ratings`, `matches`, `web_sync_queue` (nessa ordem, por causa das FKs), `UPDATE players SET rating = p_initial_rating, matches = 0, wins = 0, losses = 0, kills = 0, deaths = 0, assists = 0, damage = 0, mvps = 0, updated_at = now()` (mantém as linhas — cadastro do jogador não é apagado) e `UPDATE web_sync_state SET wipe_pending = true`.

O corpo `plpgsql` completo de cada função fica a cargo de quem aplica o schema no Supabase — a assinatura e o comportamento acima são o contrato que `SupabaseDatabaseService` espera.

## Mapeamento dos 23 métodos de `IDatabaseService`

| Método | Chamada Supabase |
|---|---|
| `GetOrCreatePlayerAsync` | `GET players?steamid=eq.X`; se vazio, `POST players` (insert); se nome mudou, `PATCH players?steamid=eq.X` |
| `GetPlayerAsync` | `GET players?steamid=eq.X` |
| `GetTopPlayersAsync(count)` | `GET players?matches=gt.0&order=rating.desc&limit=N` |
| `GetPlayerRankPositionAsync` | `GET players?matches=gt.0&select=steamid,rating&order=rating.desc` — posição calculada em C# a partir da lista ordenada |
| `GetTotalRankedPlayersAsync` | `GET players?matches=gt.0&select=steamid` com header `Prefer: count=exact`, lê o total do header `Content-Range` da resposta |
| `GetPlayerTotalRoundsPlayedAsync` | RPC `rpc/get_player_total_rounds` (SUM não é possível via filtro REST simples) |
| `GetPlayersBySteamIdsAsync` | `GET players?steamid=in.(id1,id2,...)` |
| `GetActiveSeasonIdAsync` | `GET seasons?is_active=eq.true&select=id&limit=1` |
| `GetLastRatingChangeAsync` | `GET rating_history?steamid=eq.X&order=id.desc&limit=1` (select plano, sem embedding — `StatsCommand.cs` busca a partida separadamente via `GetMatchByIdAsync` quando precisa) |
| `GetRatingHistoryAsync` | `GET rating_history?steamid=eq.X&order=id.desc&limit=N` |
| `GetMatchByIdAsync` | `GET matches?id=eq.X` |
| `UpsertWebSyncQueueAsync` | `POST web_sync_queue` com header `Prefer: resolution=merge-duplicates` |
| `GetPendingWebSyncEntriesAsync(limit)` | `GET web_sync_queue?limit=N` |
| `ClearWebSyncQueueEntriesAsync` | `DELETE web_sync_queue?steamid=in.(...)` |
| `IsWipePendingAsync` | `GET web_sync_state?id=eq.1&select=wipe_pending` |
| `SetWipePendingAsync` / `ClearWipePendingAsync` | `PATCH web_sync_state?id=eq.1` |
| `WriteMatchEndResultAsync` | RPC `rpc/write_match_end_result` |
| `SetPlayerRatingWithAuditAsync` | RPC `rpc/set_player_rating` |
| `ResetPlayerWithAuditAsync` | RPC `rpc/reset_player` |
| `AdjustPlayerRatingWithAuditAsync` | RPC `rpc/adjust_player_rating` |
| `ResetAllDataWithAuditAsync` | RPC `rpc/reset_all_data` |
| `InitializeAsync` | Vira um `GET players?limit=1` de "ping" — loga erro se falhar, não lança (ver Tratamento de erros) |

## Componentes (C#)

- **`RankingConfig`** — novos campos: `SupabaseUrl` (string), `SupabaseServiceKey` (string). Nenhum campo de `WebSync*` muda.
- **`MixRankingPlugin.Load()`** — troca `new DatabaseService(dbPath)` + `InitializeAsync()` (que criava tabelas) por `new SupabaseDatabaseService(Config, Logger)`; `InitializeAsync()` vira um ping não-fatal (loga erro, plugin continua carregando).
- **`RatingService`, `PlayerService`, `WebSyncService`, `AdminCommands`** — assinatura de construtor muda de `DatabaseService` para `IDatabaseService`. Nenhuma outra mudança de lógica.

## Tratamento de erros

- **Comandos de leitura** (`!rank`, `!stats`, `!top`, `!lastmatch`, `!profile`): já têm `try/catch` no handler hoje (ex: `RankCommand.cs:75-86`). `SupabaseDatabaseService` deve lançar exceção em falha HTTP/timeout — o catch existente já cobre a UX ("Erro ao buscar ranking").
- **Fim de partida** (`WriteMatchEndResultAsync`): a escrita mais crítica, já que os stats só existem em memória até esse ponto. `SupabaseDatabaseService` faz retry com backoff curto dentro do próprio método (3 tentativas, ~1s/2s/4s) antes de propagar a exceção. Se falhar de vez, `MatchEvents.OnMatchEnd` (`Match/MatchEvents.cs:252-281`) hoje só loga via `_logger.LogError` no catch — adicionar uma mensagem de chat visível (`Server.PrintToChatAll`) tipo "Erro ao salvar resultado da partida, contate um admin", já que hoje esse erro fica invisível pros jogadores.
- **Boot do plugin**: falha de conectividade no `InitializeAsync()`/ping não derruba o `Load()` — loga erro e segue (comandos individuais falham com a mensagem de erro já existente até a conectividade voltar).
- **Timeout do `HttpClient`**: curto (5s, mesmo valor já usado em `HttpWebSyncClient`), pra não travar comandos de chat nem o processamento de fim de partida por muito tempo.

## Segurança

- **Chave de API:** `SupabaseServiceKey` é uma `service_role key` do Supabase (bypassa RLS) — mesmo tratamento de risco que `WebSyncApiKey` já recebe hoje (vive em JSON no host, fora do controle de proteção deste projeto; mitigação é blast radius, não exposição zero). Nunca logada. Como esse Supabase é dedicado só a este plugin, o blast radius de um vazamento fica contido a esses dados (não expõe a plataforma web nem outros sistemas).
- **Transporte:** HTTPS obrigatório (padrão da REST API do Supabase).
- **RLS:** desligado ou policy restrita à `service_role`, já que não há acesso de usuário final a essas tabelas.

## Testes

- `IDatabaseService` + `FakeDatabaseService` substituem o setup de SQLite em memória usado hoje em `WebSyncServiceTests.cs` e partes de `RatingTests.cs`.
- `DatabaseTests.cs` (testa SQL direto contra SQLite) é removido.
- `SupabaseDatabaseServiceTests.cs` novo: usa um `HttpMessageHandler` fake injetado no `HttpClient` pra verificar a montagem de request (URL, headers, corpo) de uma amostra representativa dos métodos — um GET simples, um upsert, uma chamada RPC — sem bater num Supabase real. Não precisa cobrir os 23 métodos exaustivamente.

## Fora de escopo

- Aplicar o schema/funções RPC no Supabase — responsabilidade de quem mantém esse lado, fora deste repositório.
- Migrar dados históricos do `mixranking.db` antigo (já se provou não-confiável; ranking recomeça do zero no Supabase).
- RLS granular por usuário final — não há usuários finais acessando essas tabelas diretamente.
- Mudança de comportamento do `WebSyncService`/sync com a plataforma web — permanece idêntico.
- Cache local de leitura — descartado no brainstorming (ganho de latência pequeno pra chat, complexidade adicional sem necessidade real).

## Atualização de `docs/integration-contract.md`

Após a implementação, adicionar nota no topo do documento indicando que a seção 2 (schema SQLite) também deixa de refletir o armazenamento real do plugin — o schema ativo passa a ser o Postgres do Supabase dedicado, documentado neste spec.
