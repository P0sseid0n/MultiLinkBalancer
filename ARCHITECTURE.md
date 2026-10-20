# Arquitetura

O MultiLink é um `.exe` único com duas partes:

- **App em C#** (`src/`): proxy, balanceamento, monitoramento e integração com o Windows.
  Compilado pelo `csc.exe` do .NET Framework (sem projeto `.csproj`; ver `build.ps1`).
- **Interface em Svelte** (`ui/`): compilada num único `index.html`, embutido no `.exe` e
  mostrado pelo WebView2. A interface não acessa a rede: conversa só com o app em C#.

```
                ┌──────────────── Ui ─────────────────┐
                │ MainWindow · TrayIcon · WebViewHost │ ← janela, bandeja e ponte com a interface web
                └──────────────────┬──────────────────┘
                                   │ IAppHost (avisos, diálogos, sair)
                ┌──────────────────▼──────────────────┐
                │ App: AppController · LinkManager    │ ← orquestra: comandos, ciclo de vida, estado da UI
                └───┬──────────┬──────────┬───────────┘
        ┌───────────┘          │          └───────────────┐
┌───────▼──────┐  ┌────────────▼─────────┐  ┌─────────────▼─────────┐
│ Proxy        │  │ Monitoring           │  │ Downloads · Updates   │
│ SOCKS/HTTP   │  │ saúde, quedas,       │  │ download acelerado,   │
│ e relé UDP   │  │ histórico, medição   │  │ atualização           │
└───────┬──────┘  └────────────┬─────────┘  └─────────────┬─────────┘
        └──────────────┬───────┴──────────────────────────┘
               ┌───────▼───────┐   ┌──────────────────────────────┐
               │ Links         │   │ Platform                     │
               │ Link, Balancer│   │ PowerShell, rotas, proxy do  │
               └───────────────┘   │ Windows, captura (tun2socks) │
                                   └──────────────────────────────┘
               Common (log de eventos, extensões de socket/stream) · Storage (config.ini, pastas)
```

## Regras de dependência

Cada pasta de `src/` é um namespace (`MultiLink.<Pasta>`). As dependências só descem:

| Camada | Pode usar | Não pode usar |
|---|---|---|
| `Common`, `Storage` | só o .NET | nada do app |
| `Links` | `Common` | Proxy, Monitoring, Platform, App, Ui |
| `Platform` | `Common`, `Storage` | Links, App, Ui |
| `Proxy`, `Monitoring`, `Downloads`, `Updates` | `Links`, `Platform`, `Common`, `Storage` | App, Ui (e uns aos outros) |
| `App` | tudo acima | Ui (fala com a janela só pela interface `IAppHost`) |
| `Ui` | tudo | — |

O `App` não conhece WinForms nem WebView2; a `Ui` não tem regra de negócio: repassa comandos
ao `AppController` e publica o estado que ele devolve.

## Pastas do app (C#)

| Pasta | Responsabilidade | Principais tipos |
|---|---|---|
| `Program.cs` | Entrada: instância única, DPI, carregamento do WebView2 | `Program` |
| `Common/` | Utilidades sem regra de negócio | `ActivityLog`, `SocketExtensions`, `StreamExtensions`, `UnixTime` |
| `Storage/` | Persistência local | `AppPaths`, `AppSettings` (config.ini com propriedades tipadas) |
| `Links/` | O domínio: links e a escolha do link de cada conexão | `Link`, `LinkQuality`, `LinkTraffic`, `LinkIpv6`, `LinkHealth`, `Balancer`, `AdapterDiscovery` |
| `Proxy/` | Servidor SOCKS4/SOCKS5/HTTP na porta local e relé UDP | `ProxyServer`, `Socks4Handshake`, `Socks5Handshake`, `HttpProxyHandshake`, `BalancedConnector`, `UdpAssociation` |
| `Monitoring/` | Saúde dos links, quedas, failover pelo roteamento, histórico e medição | `HealthChecker`, `IcmpPing`, `LinkMonitor`, `RouteFailover`, `PortUsageMonitor`, `UsageHistory`, `SpeedTest` |
| `Downloads/` | Download acelerado (HTTP Range por todos os links) | `SegmentedDownload`, `DownloadManager` |
| `Updates/` | Atualização pelo GitHub Releases | `Updater` |
| `Platform/` | Tudo que mexe no Windows | `ProcessRunner`, `PowerShell`, `Elevation`, `NetworkAdapters`, `RouteMetrics`, `SystemProxy`, `StartupRegistration`, `TrafficCapture`, `NetworkProfiles`, `JobObject`, `EmbeddedFiles` |
| `App/` | Orquestração: ciclo de vida, comandos da interface, estado publicado | `AppController`, `LinkManager`, `SumTestResult`, `UiState`, `IAppHost` |
| `Ui/` | Janela nativa, bandeja, atalhos e WebView2 | `MainWindow`, `TrayIcon`, `PanelPlacement`, `GlobalHotkeys`, `WebViewHost`, `AppIcon` |

## Interface (Svelte)

```
ui/src/
  main.js, App.svelte, app.css   entrada, troca de telas e tema
  api/        bridge.js (WebView2 ↔ C#), mock.js (simulador do npm run dev)
  state/      app.svelte.js (estado global, comandos, avisos, confirmação)
  lib/        funções puras: format.js, health.js, motion.js
  components/ peças genéricas, sem saber de links: Toggle, LineChart, SplitBar, PageHeader...
  screens/    uma pasta por tela, com as peças só dela
    home/       HomeScreen, PowerButton, EventBar, LinkItem
    history/    HistoryScreen
    settings/   SettingsScreen, DownloadPanel, SettingsForm
```

Componentes de `screens/` podem usar `state/`, `api/`, `lib/` e `components/`; os de `components/`
recebem tudo por props (exceto `Toasts` e `ConfirmDialog`, que exibem o estado global).

## Contrato entre o app e a interface

A interface manda `{ id, cmd, args }` e recebe `{ type: "reply", id, ok, result | error }`.
O app também envia, sem pedido, `{ type: "state", data }` a cada segundo e `{ type: "log", entries }`.

- Comandos: `AppController.Handle` (C#) e `api/mock.js` (simulador) — os dois precisam continuar iguais.
- Medição: `measure` mede cada link e ajusta os pesos (estado `measuring`); `measure.sum` faz o teste
  da soma, todos os links juntos e depois o melhor sozinho (estado `sumTest: { running, result }`, com
  o resultado em `SumTestResult`, só na memória).
- Estado: `UiState.Build` (C#). As chaves desse JSON são o contrato com a interface; renomear
  uma chave exige mudar os dois lados e o `mock.js`.

## Dados gravados no disco

Ficam em `%APPDATA%\MultiLinkBalancer` e precisam continuar legíveis por versões antigas e novas:

- `config.ini`: as chaves (`port`, `mode`, `link.<id>`...) estão todas em `AppSettings`,
  `LinkManager`, `RouteFailover` e `SystemProxy`. Não renomeie uma chave sem migrar o valor antigo.
- `history.json`: os nomes curtos dos campos (`T`, `V`, `D`...) são o formato do arquivo; ver `UsageHistory`.

## Convenções

- Nomes descritivos: nada de variáveis de uma letra fora de índices de laço (`i`).
- Comentários em português explicam o porquê; o código diz o quê.
- Um tipo principal por arquivo, com o nome do arquivo igual ao do tipo.
- Textos mostrados ao usuário em português, com acentos.
