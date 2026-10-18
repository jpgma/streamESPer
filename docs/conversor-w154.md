# Conversor `.w154`

O `w154` transforma um vídeo qualquer no arquivo que um sender futuro transmite ao painel. O arquivo guarda índices de paleta, não RGB565. O PCM mono, quando a origem tem áudio, vai num bloco depois dos quadros.

Os nomes de campo e de estrutura permanecem em inglês, como no contrato do painel.

## Picture

| Item | Valor |
| --- | --- |
| Tamanho | 240×240, origem no canto superior esquerdo |
| Índice | um byte por pixel, `y * 240 + x` |
| Band | 30 faixas de 8 linhas. A band `i` cobre as linhas `[8i, 8i+8)` e tem 1920 bytes |
| Palette | 256 cores RGB565, 512 bytes, little-endian |
| `band_mask` | 30 bits. O bit `i` marca a band `i` |

RGB565 é `(R >> 3) << 11 | (G >> 2) << 5 | (B >> 3)`, com `R`, `G` e `B` de 0 a 255. O painel inverte a cor na inicialização; o arquivo guarda o valor de antes dessa inversão.

## Proporção 1:1

A origem quase nunca é quadrada. O conversor escolhe um `fit` antes de quantizar:

- `cover` recorta o centro até um quadrado e reamostra para 240×240. É o padrão.
- `contain` encaixa o quadro inteiro dentro de 240×240 e preenche o resto com preto. `palette[0]` fica RGB565 0, e as barras usam o índice 0.

Redução usa média de área. Ampliação usa bilinear.

## GOP, keyframe e delta

`fps` é inteiro, de 1 a 60.

```text
frame_us = (1000000 + fps / 2) / fps
gop = max(1, (1000000 + frame_us / 2) / frame_us)
```

24 fps grava `frame_us` 41667 e `gop` 24. 30 fps grava 33333 e 30. Há um keyframe por segundo. O quadro `n` é apresentado em `n * frame_us`.

Cada GOP ganha uma palette só. As cores da origem que caem no mesmo RGB565 se fundem. Se ainda houver mais de 256, um median cut em OKLab parte a caixa de maior erro pelo eixo de maior variância, no ponto que menos erra, e quatro passes de k-means afinam os centros. Cada centro volta para a cor RGB565 mais próxima que caiu nele, então as entradas usadas não se repetem. A palette do GOP anterior fica enquanto o erro dela não passa de 10% do corte novo. Passou da margem, outros quatro passes de k-means partem dessas cores; se o erro ainda passar de 10%, vale o corte novo. Um corte de cena costuma cair nesse caso. Os quadros do GOP são mapeados por essa palette, então os índices de um delta continuam comparáveis. Mudança de palette só acontece em keyframe.

Um keyframe traz os bits 0 e 1 de `flags`, `band_count` 30, `band_mask` `0x3FFFFFFF`, a palette e as 30 bands. Um delta não traz palette. O bit `i` fica ligado só quando a band `i` difere do quadro anterior. Um delta sem bands é um quadro parado: o player deixa a GRAM quieta.

`--dither` escolhe o mapeamento. O padrão é `none`: cada pixel vai para a entrada mais próxima em OKLab. `ordered` soma um deslocamento de Bayer 8×8, fixo pela posição, antes da busca. O mesmo quadro gera os mesmos índices, e um trecho parado continua comprimindo. `fs` é Floyd–Steinberg a partir do RGB24, com varredura alternada. Ele segura degradê e suja quase toda band, então o RLE rende menos. `--dither` sem valor é `fs`. O rate check continua valendo.

## Codecs da band

Para cada band o conversor tenta RLE e um bloco LZ4, sem frame header. Resultado com `comp_bytes` de 1920 ou mais é descartado. O menor fica. Empate fica com RLE. Se nenhum servir, a band vai crua.

| `codec` | Conteúdo |
| --- | --- |
| 0 | 1920 índices crus |
| 1 | RLE da band |
| 2 | um bloco LZ4 |

O RLE lê um `ctl`. De 0 a 127 é literal de `ctl + 1` bytes. De 128 a 255 repete um byte `ctl - 127` vezes. 1920 bytes do índice 7 viram quinze pares `FF 07`.

O bloco LZ4 é o que `LZ4_decompress_safe` expande para exatamente 1920 bytes. Cada record termina com zeros até múltiplo de 4.

O `crc32` do body é o CRC do PNG/zlib: polinômio `0xEDB88320`, init `0xFFFFFFFF`, xor final `0xFFFFFFFF`.

## Arquivo

Extensão `.w154`. Inteiros multi-byte são little-endian.

| Offset | Campo |
| --- | --- |
| 0 | magic `W154` |
| 4 | `version` = 1 |
| 6 | `header_size` = 32 |
| 8 | `width` = 240 |
| 10 | `height` = 240 |
| 12 | `band_rows` = 8 |
| 14 | `band_count_max` = 30 |
| 16 | `frame_us` |
| 20 | `frame_count` |
| 24 | `flags` = 0 |
| 28 | `audio_offset` = 0, ou o offset do bloco de PCM |

No byte 32 há `frame_count` offsets `u32`, cada um apontando para o `body_bytes` daquele quadro. O primeiro offset é `header_size + frame_count * 4`. Em seguida:

```text
u32 body_bytes
u8  body[body_bytes]
```

`body_bytes` fica no arquivo. O body é o que o sender põe no ar. Um body maior que 58244 é recusado.

Se a origem tem um stream de áudio e `--no-audio` não foi pedido, o conversor grava PCM mono s16le depois do último quadro. `audio_offset` aponta para esse bloco. Sem áudio na origem, o campo fica 0 e o arquivo acaba no último body. A taxa é 16000, 24000 ou 48000. O padrão é 48000.

```text
timeline_us  = frame_count * frame_us
sample_count = (timeline_us * sample_rate + 500000) / 1000000
pcm_bytes    = sample_count * 2
```

Amostra 0 é o quadro 0. PCM mais curto termina em silêncio. PCM mais longo é cortado. O bloco tem 16 bytes (`pcm_bytes`, `sample_rate`, `channels` 1, `sample_format` 0, `crc32` do PCM) e em seguida as amostras little-endian. A extração é um segundo ffmpeg: `-map 0:a:0 -ac 1 -ar <taxa> -acodec pcm_s16le -f s16le`.

Depois de gravar, o conversor relê o arquivo: magic, offsets, CRC, keyframes no múltiplo de `gop`, decode das bands, diferença real de cada delta e, se houver, o bloco de áudio. Se a verificação falha, o `.w154` é apagado.

## Rate check

O conversor não escreve datagramas. Ele estima o payload UDP como o sender faria, fatiando cada body em pedaços de 1456 bytes e somando o header de 16 bytes de cada fragmento:

```text
frags     = ceil(body_bytes / 1456)
udp_bytes = body_bytes + 16 * frags
```

A média e o pior segundo (janela alinhada em `frame_us`) saem em Mbit/s. O PCM do mesmo segundo entra na soma: `ceil(bytes_do_segundo / 1456)` fragmentos, cada um com 16 bytes de cabeçalho. Um segundo cheio a 48000 Hz mono são 96000 bytes de amostra, 66 fragmentos e 97056 bytes UDP, cerca de 0,78 Mbit/s.

| Pior segundo | Leitura |
| --- | --- |
| até 16 | confortável, sucesso padrão |
| 16 a 21 | intermitente |
| acima de 21 | estoura o rádio medido |

O arquivo é gravado mesmo acima de 16. Sem `--allow-over-budget`, o processo sai com código 2.

## Uso

Sem `--input`, o programa pergunta o arquivo, a saída, o fps, o `fit`, o dither (`none`, `ordered` ou `fs`), se o áudio entra e a taxa.

Num terminal, a conversão mostra uma barra com a porcentagem, os quadros feitos, o tempo decorrido e o tempo restante. A estimativa usa a duração que o ffmpeg lê na origem. Se essa duração não existir, a linha mostra só os quadros e o tempo decorrido.

```text
w154
w154 --input video.mp4 --output video.w154 --fps 24 --fit cover
w154 --input video.mp4 --dither ordered
w154 --input video.mp4 --fit contain --dither fs --allow-over-budget
w154 --input video.mp4 --sample-rate 16000 --no-audio
w154 selftest
```

O ffmpeg procurado é, nesta ordem, o `ffmpeg.exe` ao lado do programa, `tools/ffmpeg/win-x64/ffmpeg.exe` subindo diretórios, e o `PATH`. A build pinada e a licença estão em `tools/ffmpeg/PROVENIENCIA.txt`. Sem `--input`, o programa também pergunta a taxa de áudio (padrão 48000) e se o PCM entra no arquivo. `--sample-rate` aceita 16000, 24000 ou 48000. `--no-audio` deixa o arquivo só com vídeo.
