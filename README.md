# OBS Studio para o WigiDash (OpenWigiDashManager)

Plugin do OBS Studio para o painel WigiDash da G.SKILL, no
[OpenWigiDashManager](https://github.com/OpenWigiDash/OpenWigiDashManager) (.NET 10, SDK 3.2). Controla o OBS
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
| OBS: Painel de cenas | Troca para a cena tocada (no modo estúdio, para a prévia) | Manda direto para o ar | Large, com tela cheia |
| OBS: Status | | Com o OBS fechado neste PC, abre o OBS (só em conexão local) | Small, Wide |
| OBS: Fonte visível | Mostra e esconde uma fonte da cena | Inverte só enquanto segura | Mini, Small |
| OBS: Filtro | Liga e desliga um filtro de uma fonte ou cena | Inverte só enquanto segura | Mini, Small |
| OBS: Modo estúdio | Liga e desliga o modo estúdio | | Mini, Small |
| OBS: Transição | Manda a prévia para o ar com a transição atual | | Mini, Small |
| OBS: Mídia | Play e pause (parada, começa do início) | Reinicia | Small, Wide |
| OBS: Coleção de cenas | Só avisa "Segure para trocar" (opção: tocar já troca) | Troca para a coleção tocada | Small, Wide, Large, com tela cheia |
| OBS: Perfil | Só avisa "Segure para trocar" (opção: tocar já troca) | Troca para o perfil tocado (bloqueado com saída ligada) | Small, Wide, Large, com tela cheia |

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
- **Coleção de cenas** e **Perfil**: grade com a atual em verde e, embaixo, o que o toque faz. Durante a troca, a que
  está entrando ganha anel azul e o tile mostra "Trocando…" até o OBS confirmar, ignorando novos toques. Com live ou
  gravação rodando, a coleção avisa em amarelo (a troca recarrega cenas e fontes) e o perfil fica bloqueado em vermelho,
  dizendo qual saída parar (o perfil muda servidor, chave, encoder e pasta de gravação).
- **Painel de cenas**: todas as cenas numa grade, a do ar em verde e a da prévia com anel azul. Arraste para cima para
  abrir em tela cheia e para baixo para voltar. Com mais cenas do que cabem, a última célula vira "Mais" e troca a
  página; na tela cheia, arrastar para os lados também troca.
- **Status**: CPU e FPS do OBS e quadros perdidos (no Wide, separados em render e codificação, com o gráfico da CPU).
  Mostra "Ao vivo" ou "Gravando" com o tempo. Perdidos acima de 1% ficam amarelos, acima de 5% vermelhos.
- **Fonte visível**: o nome da fonte, a cena e o ícone pelo tipo (câmera, captura, navegador, texto, imagem, mídia).
  Escondida aparece cortada.
- **Filtro**: ligado (roxo) ou desligado, com a fonte embaixo.
- **Modo estúdio**: ligado mostra qual cena está na prévia.
- **Transição**: o nome e a duração da transição, a cena que vai entrar e "Em transição…" enquanto roda.
- **Mídia**: tocando (verde), pausado (amarelo) ou parado, com a barra de progresso e o tempo que falta.

Os toques que encerram algo (parar a live, desligar o buffer) pedem long-press, com a barra enchendo embaixo do tile.
Quando o OBS recusa um comando, o tile fica vermelho por alguns segundos com o motivo.

Sem o OBS pronto, todos os tiles mostram o porquê: **OBS fechado** (conexão neste PC), **Sem resposta** (conexão com
outro PC: pode ser o PC desligado, o OBS fechado ou o firewall), **Falta a senha**, **Senha errada**, **Atualize o OBS**
ou **Conectando…**. Eles voltam sozinhos quando o OBS responde.

## Conexões

Na página do plugin no OpenWigiDash: **Loja de plugins > Instalados > OBS > Configurar**. Dá para salvar mais de uma
conexão, por exemplo o OBS deste PC e o OBS de outro PC da rede, cada uma com:

- **Nome**: como ela aparece na escolha de cada widget (ex.: *Este PC*, *PC da live*).
- **Endereço e porta**: `127.0.0.1` e `4455` quando o OBS roda no mesmo PC; o IP ou o nome do outro PC quando não.
  No outro PC, a porta do servidor WebSocket precisa estar liberada no firewall do Windows.
- **Senha**: no OBS, *Ferramentas > Configurações do servidor WebSocket > Mostrar informações de conexão*.
  Em conexão local, o botão **Detectar neste PC** lê a porta e a senha do arquivo do próprio OBS
  (`%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json`), só quando é clicado.

Uma das conexões é a **padrão**. Na tela de cada widget, o campo **Conexão** (aparece com duas ou mais) escolhe qual OBS
ele controla; quem não escolheu segue a padrão. Assim uma página do Canvas pode ficar toda no OBS deste PC e outra no OBS
do outro PC. Se a conexão de um widget for removida, ele passa para a padrão. Cada conexão só abre enquanto algum widget
dela está na tela (ou a página do plugin está aberta).

O plugin não procura o processo do OBS: o estado vem só da conexão WebSocket. O que só vale para o OBS deste PC (dizer
**OBS fechado**, abrir o OBS segurando o Status e **Detectar neste PC**) fica só para conexão local: `127.0.0.1`,
`localhost`, o nome deste PC ou um IP dele.

As conexões ficam em `%LOCALAPPDATA%\OpenWigiDash\WidgetData\OBS\connections.json`, com a senha criptografada pelo DPAPI
do Windows (só o seu usuário, neste PC, consegue abrir). Ela não vai para o log. Quem usava a versão com uma conexão só
encontra a de antes (`connection.json`) como a conexão padrão, sem refazer nada. O log de conexão fica em `obs.log`, na
mesma pasta, com o id da conexão em cada linha.

### O que mudou em relação ao plugin do OBS no Stream Deck

| No Stream Deck | Aqui |
| --- | --- |
| Botão quadrado com ícone e texto pequeno | Tile com o estado em destaque: tempo, dB, cena, bitrate |
| Sem nível de áudio | Medidor ao vivo no mixer |
| Sem miniatura da cena | Miniatura ao vivo no Wide e no Large |
| Parar a live com um toque sem querer | Encerrar pede long-press |
| Precisa do plugin do Elgato instalado no OBS | Usa o obs-websocket que já vem no OBS |

Os ícones dos tiles são vetores do próprio plugin (gravar, live, microfone, replay, cena...), desenhados para a tela escura do
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
eventos, pedidos recusados, medidor, miniatura, itens de cena, filtros, mídia, estatísticas, modo estúdio e transição. O desenho (GDI+) só roda no Windows; o que cada tile mostra é testado
sem desenhar.

Para compilar contra o SDK do repo do Manager clonado ao lado: `-p:UseLocalOpenWigiSdk=true`.

Publicação: workflow **Release Widget** (`release.yml`) na `main`, com a versão sem o "v".

Plano e próximos widgets: [docs/PLANO.md](docs/PLANO.md).
