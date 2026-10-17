# StreamESPer

Solution .NET 10. O primeiro projeto é o conversor `w154`, que grava vídeo no formato do painel Waveshare 1,54". A API de streaming entra depois, na mesma solution.

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
