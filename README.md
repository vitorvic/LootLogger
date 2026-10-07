# LootLogger

Loot logger para Albion Online, para qualquer jogador ou guild. Lê o tráfego de rede do jogo (só leitura, não mexe no cliente) e mostra quem pegou o quê, de quem, em qual mapa e quando.

## O que faz

- **Painel**: itens capturados, última hora, tempo ativo, valor estimado e eventos recentes.
- **Loot Log**: tabela com busca, filtros e estatísticas.
- **Comparar Baú**: junte os logs de várias pessoas, cole o log do baú da guild e veja quem guardou, quem está devendo e quem morreu com o item.
- **Configurações**: Modo Party e português/inglês.
- **Exportar CSV** no mesmo formato que as guilds já usam. Cada sessão também é salva sozinha, linha por linha.

## Requisitos

- Windows 10 ou 11.
- Abrir como administrador (o Windows pergunta ao abrir). Assim ele lê o tráfego do jogo sozinho, inclusive com ExitLag.
- Não precisa de Npcap.
- Para compilar: Visual Studio 2026 Community com a carga ".NET desktop development" (.NET 10).

## Como rodar pelo código

1. Abra o Visual Studio (ou o PowerShell) como administrador e abra `LootLogger.slnx`.
2. Escolha `LootLogger.App` como projeto de inicialização.
3. Aperte F5.

Testes: `dotnet test` na pasta do projeto.

## Onde ficam os arquivos

- Configurações, lista de itens e gravações: `%AppData%\LootLogger`
- Sessões exportadas: `Documentos\LootLogger\Sessões` (dá para trocar em Configurações)
- Erros inesperados: `%AppData%\LootLogger\erros.log`

## Quando o jogo atualizar

Os códigos de rede do Albion mudam às vezes depois de um patch. Se o programa parar de registrar loot, os códigos podem ser ajustados sem recompilar em `%AppData%\LootLogger\codes.json` (é criado na primeira vez que o programa abre).

## Estrutura

| Pasta | O que tem |
|---|---|
| `src/LootLogger.Protocol` | Leitura do protocolo Photon do jogo |
| `src/LootLogger.Capture` | Captura de pacotes com Npcap |
| `src/LootLogger.Core` | Regras do loot, CSV, Comparar Baú, lista de itens |
| `src/LootLogger.App` | O programa (WPF) |
| `tests/LootLogger.Core.Tests` | Testes |

## Licença

GPL-3.0. Veja [LICENSE](LICENSE) e [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Projeto de fã, sem vínculo com a Sandbox Interactive GmbH. Albion Online é marca da Sandbox Interactive.
