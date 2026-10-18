# Conversor `.w154`

O `w154` transforma um vídeo qualquer no arquivo da versão 2. O arquivo guarda índices de paleta, uma linha por record, não RGB565. O áudio, quando a origem tem, vai em IMA ADPCM depois dos quadros. O host expande para s16le na hora de enviar.

Os nomes de campo e de estrutura permanecem em inglês, como no contrato do painel.

## Picture

| Item | Valor |
| --- | --- |
| Tamanho | 240×240, origem no canto superior esquerdo |
| Índice | um byte por pixel, `y * 240 + x` |
| Linha | 240 índices. O record `i` é a linha `y = i` |
| Palette | até 256 cores RGB565, sempre 512 bytes, little-endian |
| `row_mask` | 30 bytes, 240 bits. O bit `i` marca a linha `i` |

RGB565 é `(R >> 3) << 11 | (G >> 2) << 5 | (B >> 3)`, com `R`, `G` e `B` de 0 a 255. O painel inverte a cor na inicialização; o arquivo guarda o valor de antes dessa inversão.

## Proporção 1:1

A origem quase nunca é quadrada. O conversor escolhe um `fit` antes de quantizar:

- `cover` recorta o centro até um quadrado e reamostra para 240×240. É o padrão.
- `contain` encaixa o quadro inteiro dentro de 240×240 e preenche o resto com preto. `palette[0]` fica RGB565 0, e as barras usam o índice 0.

Redução usa média de área. Ampliação usa bilinear.

## GOP, keyframe e delta

`fps` é inteiro, de 1 a 80. O padrão continua 24. `frame_us` 12500 é 80 fps.

```text
frame_us = (1000000 + fps / 2) / fps
gop = max(1, (1000000 + frame_us / 2) / frame_us)
```

24 fps grava `frame_us` 41667 e `gop` 24. 80 fps grava 12500 e 80. Há um keyframe por segundo. O quadro `n` é apresentado em `n * frame_us`.

Cada GOP ganha uma palette só, com teto de 256, 128, 64 ou 32 cores conforme a escada de taxa. As cores da origem que caem no mesmo RGB565 se fundem. Se ainda houver mais cores que o teto, um median cut em OKLab parte a caixa de maior erro pelo eixo de maior variância, no ponto que menos erra, e quatro passes de k-means afinam os centros. Cada centro volta para a cor RGB565 mais próxima que caiu nele, então as entradas usadas não se repetem. A palette do GOP anterior fica enquanto o erro dela não passa de 10% do corte novo. Passou da margem, outros quatro passes de k-means partem dessas cores; se o erro ainda passar de 10%, vale o corte novo. Um corte de cena costuma cair nesse caso. Depois do corte, os slots usados são ordenados pela luminância OKLab. `contain` deixa o preto no slot 0 e o LUT é refeito. Os quadros do GOP são mapeados nessa ordem, então os índices de um delta continuam comparáveis. Mudança de palette só acontece em keyframe.

No mapeamento, o índice do quadro anterior fica quando o erro OKLab daquela cor está dentro da margem do melhor candidato. A margem inicial é cerca de um passo de verde no RGB565. Floyd–Steinberg na primeira passagem não usa esse hold.

Um keyframe traz os bits 0 e 1 de `flags`, `row_count` 240, `row_mask` com 30 bytes `0xFF`, a palette e as 240 linhas. Um delta não traz palette. O bit `i` fica ligado só quando a linha `i` difere do quadro anterior. Um delta sem linhas é um quadro parado: o player deixa a GRAM quieta.

`--dither` escolhe o mapeamento. O padrão é `none`: cada pixel vai para a entrada mais próxima em OKLab. `ordered` soma um deslocamento de Bayer 8×8, fixo pela posição, antes da busca. O mesmo quadro gera os mesmos índices, e um trecho parado continua comprimindo. `fs` é Floyd–Steinberg a partir do RGB24, com varredura alternada. Ele segura degradê e suja mais linhas. `--dither` sem valor é `fs`. O rate check continua valendo, e o Floyd–Steinberg que estoura 16 Mbit/s desce a escada como os outros.

## Codecs da linha

Para cada linha suja o conversor tenta RLE absoluto, RLE do delta com o pixel da esquerda no keyframe, e RLE de resíduo com deslocamento de −16 a 16 no delta. Resultado com `comp_bytes` de 240 ou mais é descartado. O menor fica. Empate fica com o codec tentado antes. Se nenhum servir, a linha vai crua.

| `codec` | Conteúdo |
| --- | --- |
| 0 | 240 índices crus, `dx` 0 |
| 1 | RLE dos índices, `dx` 0 |
| 2 | RLE do resíduo `index[x] - pred[x]` depois do deslocamento, só em delta |
| 3 | RLE de `index[x] - index[x - 1]`, só em keyframe |

O preditor fora da linha gruda no pixel da borda. A soma é módulo 256. O RLE lê um `ctl`. De 0 a 127 é literal de `ctl + 1` bytes. De 128 a 255 repete um byte `ctl - 127` vezes. 240 bytes do índice 7 viram `FF 07 EF 07`. Cada record termina com zeros até múltiplo de 4. Não há LZ4.

O `crc32` do body é o CRC do PNG/zlib: polinômio `0xEDB88320`, init `0xFFFFFFFF`, xor final `0xFFFFFFFF`.

## Arquivo

Extensão `.w154`. Inteiros multi-byte são little-endian.

| Offset | Campo |
| --- | --- |
| 0 | magic `W154` |
| 4 | `version` = 2 |
| 6 | `header_size` = 32 |
| 8 | `width` = 240 |
| 10 | `height` = 240 |
| 12 | `band_rows` = 1 |
| 14 | `band_count_max` = 240 |
| 16 | `frame_us` |
| 20 | `frame_count` |
| 24 | `flags` = 0 |
| 28 | `audio_offset` = 0, ou o offset do bloco de PCM |

No byte 32 há `frame_count` offsets `u32`, cada um apontando para o `body_bytes` daquele quadro. O primeiro offset é `header_size + frame_count * 4`. Em seguida:

```text
u32 body_bytes
u8  body[body_bytes]
```

`body_bytes` fica no arquivo. O body é o que o sender põe no ar. Um body menor que 38 ou maior que 59110 é recusado. Um delta vazio tem 38 bytes.

Se a origem tem um stream de áudio e `--no-audio` não foi pedido, o conversor extrai s16le e grava IMA ADPCM mono depois do último quadro. `audio_offset` aponta para esse bloco. Sem áudio na origem, o campo fica 0 e o arquivo acaba no último body. A taxa é 16000, 24000 ou 48000. O padrão é 48000. `sample_format` no arquivo é 1.

```text
timeline_us  = frame_count * frame_us
sample_count = (timeline_us * sample_rate + 500000) / 1000000
pcm_bytes    = payload IMA de sample_count
```

Amostra 0 é o quadro 0. PCM mais curto termina em silêncio. PCM mais longo é cortado. O bloco tem 16 bytes (`pcm_bytes`, `sample_rate`, `channels` 1, `sample_format` 1, `crc32` do ADPCM) e em seguida os blocos. Um bloco cheio tem 256 bytes e 505 amostras. A extração continua sendo um segundo ffmpeg: `-map 0:a:0 -ac 1 -ar <taxa> -acodec pcm_s16le -f s16le`.

Depois de gravar, o conversor relê o arquivo: magic, offsets, CRC, keyframes no múltiplo de `gop`, decode das linhas, diferença real de cada delta e, se houver, o bloco de áudio. Se a verificação falha, o `.w154` é apagado.

## Rate check

O conversor não escreve datagramas. Ele estima o payload UDP como o sender faria, fatiando cada body em pedaços de 1456 bytes e somando o header de 16 bytes de cada fragmento:

```text
frags     = ceil(body_bytes / 1456)
udp_bytes = body_bytes + 16 * frags
```

A média e o pior segundo (janela alinhada em `frame_us`) saem em Mbit/s. O áudio do mesmo segundo entra como s16 expandido, não como o ADPCM do arquivo: `ceil(bytes_do_segundo / 1456)` fragmentos, cada um com 16 bytes de cabeçalho. Um segundo cheio a 48000 Hz mono são 96000 bytes de amostra, 66 fragmentos e 97056 bytes UDP, cerca de 0,78 Mbit/s.

Se o pior segundo passa de 16, o conversor refaz o ffmpeg e codifica de novo. Cada passagem é uma linha no log (`passe N: C cores, hold S`). A escada é hold 1 com 256 cores (Floyd–Steinberg sem hold), hold 4, hold 16, e depois tetos de 128, 64 e 32 cores ainda com hold 16. Para no primeiro segundo que cabe. No chão o arquivo continua gravado.

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
