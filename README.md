# StreamESPer

Solution .NET 10. O conversor `w154` grava vídeo no formato do painel Waveshare 1,54". A lib `W154Format` é o container compartilhado. A API `W154Stream` manda esse arquivo por UDP, e o firmware em `firmware/w154` apresenta no painel.

## Conversor

`W154Convert` lê um vídeo com o ffmpeg vendido em `tools/ffmpeg/win-x64` e escreve um `.w154`: 240×240, uma paleta RGB565 por GOP, bandas de 8 linhas, e PCM mono s16le quando a origem tem áudio. O conceito está em [docs/conversor-w154.md](docs/conversor-w154.md).

Proporção 1:1:

- `cover` recorta o centro (padrão)
- `contain` encaixa o quadro e completa com barras pretas

```text
dotnet run --project W154Convert -- selftest
dotnet run --project W154Convert
dotnet run --project W154Convert -- --input video.mp4 --fps 24 --fit contain
dotnet run --project W154Convert -- --input video.mp4 --sample-rate 16000 --no-audio
```

Publicar o exe nativo:

```text
dotnet publish W154Convert -c Release
```

O processo sai com 0 quando o pior segundo fica em até 16 Mbit/s. Acima disso o arquivo ainda é gravado e a saída é 2, a menos que `--allow-over-budget` seja passado.

## Player no VLC

O plugin em `vlc-w154` abre um `.w154` no VLC 3.0.24: vídeo RGB565 e, quando o arquivo traz, PCM mono no mesmo relógio. Compilar e instalar em `plugins\demux`:

```text
powershell -File vlc-w154/build.ps1
```

No VLC, Arquivo > Abrir, ou `vlc --demux w154 video.w154`.

## Streaming

A API lê uma pasta de `.w154` e empurra o arquivo escolhido para o painel. O conceito está em [docs/api-w154.md](docs/api-w154.md). O player do device está em [docs/firmware-w154.md](docs/firmware-w154.md).

```text
dotnet run --project W154Stream -- --dir .
dotnet run --project W154Stream -- --selftest
powershell -File firmware/flash.ps1
```

`POST http://localhost:5090/play` com `{ "name": "clip.w154" }` começa o envio. O device precisa de `wifi_secrets.h` apontando para o IP do host.
