# OBS Studio para o WigiDash (OpenWigiDashManager)

Plugin do OBS Studio para o painel WigiDash da G.SKILL, no
[OpenWigiDashManager](https://github.com/OpenWigiDash/OpenWigiDashManager) (.NET 10, SDK 3.0). Controla o OBS
pelo tile e mostra o que está acontecendo nele: tempo de gravação e de live, bitrate, nível do microfone,
qual cena está no ar e a miniatura dela.

Fala com o OBS pelo **obs-websocket v5**, que já vem dentro do OBS 28 ou mais novo. Não instala nada no OBS.

## Widgets

| Widget | Toque | Segurar | Tamanhos |
| --- | --- | --- | --- |
| OBS: Gravar | Inicia e para a gravação | Pausa e retoma, ou marca um capítulo (opção) | Mini, Small, Wide |
| OBS: Live | Inicia a live | Encerra a live (tocar ao vivo só avisa) | Mini, Small, Wide |
| OBS: Cena | Troca para a cena (no modo estúdio, para a prévia) | Manda direto para o ar | Small, Wide, Large |
| OBS: Mixer de áudio | Muta e desmuta. No Wide, as bordas baixam e sobem o volume | Inverte o mudo só enquanto segura | Mini, Small, Wide |
| OBS: Replay buffer | Salva o clipe (liga o buffer se estiver desligado) | Desliga o buffer | Mini, Small |
| OBS: Câmera virtual | Liga e desliga | | Mini, Small |

O que cada tile mostra:

- **Gravar**: "Parado" ou o tempo correndo com o ponto vermelho piscando. Pausado fica amarelo. No Wide, o tamanho do arquivo.
- **Live**: pílula "Ao vivo", tempo no ar, kbps e quadros perdidos. No Wide, um gráfico do bitrate com a cor da saúde
  da live (verde até 1% de quadros perdidos, amarelo até 5%, vermelho acima ou reconectando).
- **Cena**: verde com "No ar" quando é a cena do programa, azul com "Prévia" no modo estúdio. No Wide e no Large, a
  miniatura ao vivo da cena (um quadro por segundo, só com a tela acordada).
- **Mixer**: nome da fonte, volume em dB e o medidor de nível ao vivo (desligado no Mini e com a tela dormindo).
  Modos: muta e desmuta, push to talk (segure para falar) e push to mute (segure para mutar).
- **Replay buffer**: "Salvar clipe" com o buffer ligado; depois de salvar, "Salvo" e o nome do arquivo.
- **Câmera virtual**: ligada (azul) ou desligada.

Os toques que encerram algo (parar a live, desligar o buffer) pedem long-press, com a barra enchendo embaixo do tile.
Quando o OBS recusa um comando, o tile fica vermelho por alguns segundos com o motivo.

Sem o OBS pronto, todos os tiles mostram o porquê: **OBS fechado**, **Falta a senha**, **Senha errada**,
**Atualize o OBS** ou **Conectando…**. Eles voltam sozinhos quando o OBS abre.

## Conexão

Nas configurações de qualquer widget do OBS (a conexão é a mesma para todos):

- **Endereço e porta**: `127.0.0.1` e `4455` quando o OBS roda no mesmo PC.
- **Senha**: no OBS, *Ferramentas > Configurações do servidor WebSocket > Mostrar informações de conexão*.
  O botão **Detectar neste PC** lê a porta e a senha do arquivo do próprio OBS
  (`%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json`), só quando é clicado.

A senha fica em `%LOCALAPPDATA%\OpenWigiDash\WidgetData\OBS\connection.json`, criptografada com o DPAPI do Windows
(só o seu usuário, neste PC, consegue abrir). Ela não vai para o log. O log de conexão fica em `obs.log`, na mesma pasta.

### O que mudou em relação ao plugin do OBS no Stream Deck

| No Stream Deck | Aqui |
| --- | --- |
| Botão quadrado com ícone e texto pequeno | Tile com o estado em destaque: tempo, dB, cena, bitrate |
| Sem nível de áudio | Medidor ao vivo no mixer |
| Sem miniatura da cena | Miniatura ao vivo no Wide e no Large |
| Parar a live com um toque sem querer | Encerrar pede long-press |
| Precisa do plugin do Elgato instalado no OBS | Usa o obs-websocket que já vem no OBS |

Os ícones são vetores do próprio plugin (gravar, live, microfone, replay, cena...), desenhados para a tela escura do
WigiDash. Não usam o logo nem a arte do OBS.

## Requisitos

- Windows 10 ou 11.
- [OpenWigiDashManager](https://github.com/OpenWigiDash/OpenWigiDashManager) instalado pelo
  `OpenWigiDash-Setup.exe` da [release](https://github.com/OpenWigiDash/OpenWigiDashManager/releases) dele.
- OBS Studio 28 ou mais novo, com o servidor WebSocket ligado (*Ferramentas > Configurações do servidor WebSocket*).
- Capítulos na gravação: OBS 30.2 ou mais novo, gravando em Hybrid MP4.

## Desenvolvimento

```powershell
dotnet test tests/ObsPlugin.Tests
.\deploy.ps1 -RestartManager   # publica em %LOCALAPPDATA%\OpenWigiDash\Widgets\OpenWigiDash.OBS
```

Os testes rodam contra um OBS de mentira (servidor obs-websocket local): senha certa e errada, OBS fechando e voltando,
eventos, pedidos recusados, medidor e miniatura. O desenho (GDI+) só roda no Windows; o que cada tile mostra é testado
sem desenhar.

Para compilar contra o SDK do repo do Manager clonado ao lado: `-p:UseLocalOpenWigiSdk=true`.

Publicação: workflow **Release Widget** (`release.yml`) na `main`, com a versão sem o "v".

Plano e próximos widgets: [docs/PLANO.md](docs/PLANO.md).
