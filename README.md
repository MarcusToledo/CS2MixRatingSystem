# CS2 Mix Rating System

Plugin de ranking permanente para servidores privados de Counter-Strike 2, desenvolvido com CounterStrikeSharp.

O sistema calcula a pontuação dos jogadores com base no resultado da partida e no desempenho individual, utilizando Elo com ajuste de performance.

## Recursos

- Ranking individual persistente
- Sistema Elo com swing de performance
- Histórico de partidas
- Estatísticas individuais
- Ranking dos 10 melhores jogadores
- Armazenamento em SQLite
- Configurações personalizáveis

## Comandos

### Jogadores

- `!rank` — exibe o rating e a posição no ranking
- `!top` — exibe os 10 melhores jogadores
- `!stats` — exibe as estatísticas do jogador
- `!lastmatch` — exibe o resultado da última partida
- `!profile` — exibe o perfil e o histórico recente

### Administração

Requer a permissão `@css/root`.

- `!rating_set <steamid64> <valor>`
- `!rating_reset <steamid64>`
- `!rating_add <steamid64> <valor>`
- `!rating_remove <steamid64> <valor>`
- `!rating_wipe`

## Tecnologias

- .NET 8
- CounterStrikeSharp
- SQLite

## Compilação

```bash
dotnet build MixRanking/MixRanking.csproj -c Release
```

## Status

Projeto em desenvolvimento.
