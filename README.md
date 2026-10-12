<div align="center">

<img src="src/LootLogger.App/Assets/icon.png" width="110" alt="">

# LootLogger

**Loot logger para Albion Online. Veja quem pegou o quê, de quem e quando.**

[![versão](https://img.shields.io/github/v/release/vitorvic/LootLogger?label=vers%C3%A3o&color=2ea44f)](https://github.com/vitorvic/LootLogger/releases/latest)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4)
[![grátis, GPL-3.0](https://img.shields.io/badge/gr%C3%A1tis-GPL--3.0-2ea44f)](LICENSE)

<a href="https://github.com/vitorvic/LootLogger/releases/latest/download/LootLogger-win-Setup.exe"><img src=".github/baixar.svg" width="260" alt="Baixar para Windows"></a>

<sub>Baixa direto o instalador da versão mais nova.</sub>

</div>

## Como instalar

1. Clique em **Baixar para Windows**, logo acima.
2. Abra o arquivo **LootLogger-win-Setup.exe** que baixou.
3. Pronto. O app abre sozinho e se atualiza sozinho daqui pra frente.

> [!WARNING]
> **O Windows mostrou "O Windows protegeu o computador"?**
> Clique em **Mais informações** e depois em **Executar assim mesmo**.

> [!NOTE]
> **Ele pede administrador ao abrir.** É normal: é assim que ele lê o loot do jogo. Funciona com ExitLag e não precisa de Npcap.

## O que ele faz

| | |
|---|---|
| 📊 **Painel** | Itens capturados, última hora, valor estimado e eventos recentes |
| 📋 **Loot Log** | Quem pegou cada item, de quem, em qual mapa e quando, com busca e filtros |
| 📦 **Comparar Baú** | Junta os logs de várias pessoas com o log do baú da guild: quem guardou, quem está devendo e quem morreu com o item |
| ⚔️ **Dano** | Dano, DPS, cura e dano recebido da sua party (ou só seu), com lutas salvas |
| 💾 **CSV** | Exporta no formato que as guilds já usam. Cada sessão também é salva sozinha |
| ⚙️ **Configurações** | Modo Party e português/inglês |

Só lê o tráfego de rede do jogo. Não mexe no cliente do Albion.

## Dúvidas

<details>
<summary><b>Parou de registrar depois de um patch do jogo</b></summary>

<br>

Os códigos de rede do Albion mudam às vezes depois de um patch. Normalmente sai uma versão nova do app, que se instala sozinha.

Dá também para ajustar os códigos sem esperar, em `%AppData%\LootLogger\codes.json` (é criado na primeira vez que o programa abre).

</details>

<details>
<summary><b>Onde ficam os arquivos</b></summary>

<br>

- Configurações, lista de itens e gravações: `%AppData%\LootLogger`
- Sessões exportadas: `Documentos\LootLogger\Sessões` (dá para trocar em Configurações)
- Lutas salvas do medidor de dano: `%AppData%\LootLogger\lutas-salvas.json`
- Erros inesperados: `%AppData%\LootLogger\erros.log`

</details>

<details>
<summary><b>Quero usar sem instalar</b></summary>

<br>

Na [página da versão mais nova](https://github.com/vitorvic/LootLogger/releases/latest), baixe o **LootLogger-win-Portable.zip**, extraia e abra o `LootLogger.exe`. Essa versão não se atualiza sozinha.

</details>

<details>
<summary><b>Para desenvolvedores</b></summary>

<br>

**Requisitos:** Visual Studio 2026 Community com a carga ".NET desktop development" (.NET 10).

**Rodar pelo código:**

1. Abra o Visual Studio (ou o PowerShell) como administrador e abra `LootLogger.slnx`.
2. Escolha `LootLogger.App` como projeto de inicialização.
3. Aperte F5.

Testes: `dotnet test` na pasta do projeto.

**Estrutura:**

| Pasta | O que tem |
|---|---|
| `src/LootLogger.Protocol` | Leitura do protocolo Photon do jogo |
| `src/LootLogger.Capture` | Captura de pacotes |
| `src/LootLogger.Core` | Regras do loot, CSV, Comparar Baú, lista de itens |
| `src/LootLogger.App` | O programa (WPF) |
| `tests/LootLogger.Core.Tests` | Testes |

</details>

---

<sub>GPL-3.0. Veja [LICENSE](LICENSE) e [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Projeto de fã, sem vínculo com a Sandbox Interactive GmbH. Albion Online é marca da Sandbox Interactive.</sub>
