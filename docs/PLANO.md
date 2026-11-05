# Plano do plugin OBS Studio

Plano aprovado: <https://claude.ai/artifact/LaKXkHGSTVDk6iK9yXx6T9>. Protótipos dos tiles:
<https://claude.ai/artifact/DVcKqBJM2CJcNfwFTN27Mi>.

## Decisões

- **Protocolo**: obs-websocket v5 (vem no OBS 28+). O plugin do Stream Deck usa um protocolo privado do Elgato
  que exige o plugin deles instalado no OBS; não seguimos esse caminho.
- **Uma conexão para todos os tiles** (`ObsSession.Shared`): abre com o primeiro widget e fecha com o último.
  Reconecta sozinha (1, 2, 5, 10, 30 s); com senha faltando ou errada espera 1 min, ou tenta na hora quando a pessoa salva.
- **Senha** guardada com DPAPI (usuário do Windows). "Detectar neste PC" lê o `config.json` do obs-websocket só quando clicado.
- **Medidor de áudio** (evento `InputVolumeMeters`, ~20 por segundo) só é assinado enquanto há um mixer acordado
  (Reidentify). Os tiles redesenham no máximo 10 vezes por segundo.
- **Miniatura** (`GetSourceScreenshot`, JPEG 400 px) só para cenas com tile Wide/Large acordado, um quadro por segundo.
- **Long-press** onde um toque sem querer custa caro ou onde o segundo gesto é útil: encerrar live, desligar replay,
  pausar/capítulo na gravação, mandar a cena direto para o ar, inverter o mudo enquanto segura.
- **Ícones** próprios em vetor (o SDK não tem live, replay nem cena).

## Fase 1 (este PR)

Gravar, Live, Cena (com miniatura), Mixer de áudio, Replay buffer, Câmera virtual.

## Fase 2

- Fonte: mostrar e esconder um item da cena (segurar = mostrar só enquanto segura).
- Filtro: ligar e desligar um filtro (segurar = só enquanto segura).
- Transição do modo estúdio (Cortar/Transição) e troca de coleção de cenas.
- Mídia: play/pause e tempo restante (segurar = reiniciar).
- Status do OBS: CPU, FPS, quadros perdidos de render e de codificação (segurar = abrir o OBS).
- Hotkeys do OBS por nome.

## Falta testar no aparelho

O desenho (GDI+) e o toque real no WigiDash só dão para conferir no Windows, com o OBS aberto.
