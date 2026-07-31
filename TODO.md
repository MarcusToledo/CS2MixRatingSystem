# Roadmap — CS2 Mix Rating System

Roadmap de evolução do plugin, priorizado para lançamento sem dívida de migração.
Contexto: MVP funcional em staging, ainda não exposto a jogadores reais. Formação de
times é feita por uma plataforma web externa (mesma equipe), que hoje atribui níveis
manualmente. A visão é que o rating calculado aqui alimente esses níveis no futuro.

## Fase 0 — Antes de lançar (decisões caras de reverter depois) ✅ Concluída

- [x] Criar tabela `match_player_stats` para persistir estatísticas detalhadas por
      partida/jogador (ADR, KAST real, KPR, clutches, HS%, trade kills, opening duels).
      Hoje só existem totais cumulativos em `players` e deltas grosseiros em
      `rating_history` — impossível reconstruir retroativamente "como foi minha
      partida X".
      `clutches_won`/`headshots`/`hs_percent` existem como colunas placeholder
      (sempre 0) — a detecção real fica para uma fase futura.
- [x] Adicionar `season_id` ao schema (`matches`, `rating_history`, e uma tabela de
      rating por temporada separada de `players`), mesmo que só exista 1 temporada
      no início.
- [x] Implementar audit log para ações administrativas (`rating_set`, `rating_add`,
      `rating_remove`, `rating_wipe`): quem alterou, valor antigo, valor novo, motivo,
      timestamp.
- [x] Confirmar fórmula-base definitiva de rating e decidir sobre K-factor dinâmico /
      período de placement matches (K maior nas primeiras N partidas de cada jogador).
      Implementado como K 2× nas primeiras 10 partidas, configurável.
- [x] Desenhar o contrato de dados com a plataforma web (schema/API), mesmo que a
      integração real venha depois — SteamID64 já é a chave compartilhada.
      Ver `docs/integration-contract.md`.

## Fase 1 — Lançamento

- [ ] Corrigir ADR aproximado no `!rank` (usa `Matches * 24` fixo em vez de rounds
      reais jogados).
- [ ] Corrigir cálculo estrutural do KAST no `SwingCalculator` (double counting entre
      `RoundsWithKill` e `RoundsSurvived` no mesmo round).
- [ ] Habilitar `PRAGMA journal_mode=WAL` no SQLite.
- [ ] Remover o `Task.Run(...).Wait()` síncrono no `Load()` do plugin (inicialização
      do banco).

## Fase 2 — Pós-lançamento

- [ ] Construir a integração real com a plataforma web (rating → nível).
- [ ] Detecção básica de padrões de abuso/boosting.
- [ ] Estatísticas por mapa e por dupla/parceria.
- [ ] Swing sensível a papel (role-aware: AWPer vs entry vs support).

## Quick wins (baixo esforço, independentes de fase)

- [ ] Remover ou implementar de fato `ClutchesWon` (campo existe no model, nunca é
      incrementado).
- [ ] Padronizar logging: `AdminCommands` usa `Console.WriteLine` com prefixo
      hardcoded `"GurizadaMix"` em vez de `ILogger` + `_config.ChatPrefix`, como o
      resto do código.

## Débitos técnicos

- [ ] Nenhum projeto de teste automatizado no repositório.
- [ ] Sem CI configurado.
- [ ] Cada query do `DatabaseService` abre uma nova `SqliteConnection` — dificulta
      introduzir transações (ex: inserir partida + rating changes deveria ser
      atômico e hoje não é).

## Arquitetura futura

- [ ] Extrair a fórmula de rating atrás de uma interface (`IRatingStrategy`) para
      permitir experimentar Glicko-2/K dinâmico sem reescrever `RatingService`.
- [ ] Camada de outbox para a integração externa: gravar eventos de "rating mudou"
      localmente e sincronizar com a plataforma web via processo separado, em vez de
      chamada HTTP síncrona ao fim de cada partida.
- [ ] Não trocar SQLite por um banco central agora — só migrar quando houver sinal
      real de múltiplos servidores compartilhando ranking (YAGNI).

## Nível Faceit (funcionalidades para a plataforma web, alimentadas pelo plugin)

- [ ] Gráfico de evolução de rating ao longo do tempo.
- [ ] Histórico de partidas navegável com scoreboard completo.
- [ ] Estatísticas por mapa.
- [ ] Estatísticas de parceria/dupla (com quem joga melhor).
- [ ] Badges/destaques de partida (MVP, ace, clutch 1vX) — depende de clutch
      detection de verdade.
- [ ] Elevar o piso de partidas para aparecer no leaderboard público (hoje é
      `matches > 0`).
- [ ] Tags/cores de chat por tier de rating.
