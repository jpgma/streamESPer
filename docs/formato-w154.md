# Formato `.w154` versão 1

Contrato do arquivo que um conversor grava e que um player ou um sender lê. A versão deste texto é a v1. Um leitor da v1 recusa qualquer outro `version` ou `header_size`.

O painel mostra RGB565. O arquivo guarda **índices de palette**. O Wi-Fi carrega o `body` de cada quadro, nunca um quadro RGB565 inteiro. O PCM, quando existe, fica num bloco depois do último quadro e sai em datagramas próprios.

Nomes de campo, de flag e de estrutura ficam em inglês.

## Convenções

| Tipo | Tamanho | Ordem |
| --- | --- | --- |
| `u8` | 1 | — |
| `u16` | 2 | little-endian, byte baixo primeiro |
| `u32` | 4 | little-endian, byte baixo primeiro |
| `char[4]` | 4 | bytes ASCII, sem terminador |

Divisão nos cálculos abaixo é divisão inteira, truncando em direção ao zero.

Offsets de quadro são absolutos, contados do primeiro byte do arquivo.

## Picture

| Item | Valor |
| --- | --- |
| `width` × `height` | 240 × 240 |
| Origem | canto superior esquerdo, `x` para a direita, `y` para baixo |
| Índice | um `u8` por pixel, posição `y * 240 + x` |
| Quadro indexado | 57600 bytes |
| `band` | 30 faixas de 8 linhas. A band `i` é o intervalo de linhas `[8i, 8i+8)` |
| Band crua | 1920 bytes, row-major dentro da band |
| `palette` | 256 × RGB565 = 512 bytes |
| `band_mask` | 30 bits neste arquivo. O bit `i` cobre as linhas `[8i, 8i+8)` |

O caminho de present do painel usa uma máscara de 30 bytes (240 bits, um bit por linha). Este arquivo não guarda esses 30 bytes. Quem apresenta expande o `band_mask`: o bit `i` liga os oito bits de linha `[8i, 8i+8)`.

### RGB565

`R`, `G` e `B` são 0..255. Os shifts truncam.

```text
value = (R >> 3) << 11 | (G >> 2) << 5 | (B >> 3)
```

O `u16` é gravado little-endian, a mesma ordem do DMA do painel. A inicialização do painel inverte a cor. O arquivo guarda o valor de antes dessa inversão. O player copia a entrada da palette para o buffer de saída e deixa o painel inverter.

Dois pixels de origem que caem no mesmo `value` são a mesma cor. Uma palette não repete `value` entre os slots em uso.

## Mapa do arquivo

```text
offset 0
+---------------- header (header_size = 32) ----------------+
| magic | version | sizes | frame_us | frame_count | flags |
+-----------------------------------------------------------+
offset 32
+---------------- offset table (frame_count × u32) ---------+
| offset[0] | offset[1] | ... | offset[frame_count - 1]    |
+-----------------------------------------------------------+
offset[0] = 32 + frame_count * 4
+---------------- frames, packed, sem folga ----------------+
| u32 body_bytes | body[body_bytes] | próximo quadro | ... |
+-----------------------------------------------------------+
audio_offset == 0: fim do arquivo = fim do último body
audio_offset != 0: bloco de áudio começa nesse byte e o arquivo acaba no fim do PCM
```

Não há bytes entre o header e a tabela, nem entre a tabela e o primeiro quadro, nem entre quadros. Com áudio, o bloco começa no byte seguinte ao último body.

Os offsets são `u32`. O arquivo pode passar de 2 GiB e tem de ficar abaixo de 4 GiB. No leitor .NET o comprimento de um `Span` é `int`, então body e PCM são endereçados por ponteiro mais o offset.

## Header

| Offset | Tipo | Campo | v1 |
| ---: | --- | --- | --- |
| 0 | char[4] | magic | `W154` (`57 31 35 34`) |
| 4 | u16 | `version` | 1 |
| 6 | u16 | `header_size` | 32 |
| 8 | u16 | `width` | 240 |
| 10 | u16 | `height` | 240 |
| 12 | u16 | `band_rows` | 8 |
| 14 | u16 | `band_count_max` | 30 |
| 16 | u32 | `frame_us` | período de um quadro, em microssegundos |
| 20 | u32 | `frame_count` | ≥ 1 |
| 24 | u32 | `flags` | 0 |
| 28 | u32 | `audio_offset` | 0, ou o offset absoluto do bloco de áudio |

`frame_us` aceito por um leitor está em `16667 .. 1000000` (60 fps .. 1 fps), inclusive.

Para um fps inteiro:

```text
frame_us = (1000000 + fps / 2) / fps
```

| fps | `frame_us` | bytes little-endian |
| ---: | ---: | --- |
| 24 | 41667 | `C3 A2 00 00` |
| 30 | 33333 | `35 82 00 00` |
| 1 | 1000000 | `40 42 0F 00` |
| 60 | 16667 | `3B 41 00 00` |

O player apresenta o quadro `n` em `n * frame_us` a partir do início, com `n` contado de zero.

Header de um arquivo de 24 fps com um quadro, em hex:

```text
57 31 35 34  01 00  20 00  F0 00 F0 00  08 00 1E 00
C3 A2 00 00  01 00 00 00  00 00 00 00  00 00 00 00
24 00 00 00
```

A última linha é `offset[0]`. `32 + 1 * 4 = 36` (`0x24`). Os quatro zeros do fim da segunda linha são `audio_offset`: esse exemplo não tem áudio.

## Tabela de offsets

Começa no byte `header_size`. São `frame_count` valores `u32`. `offset[i]` aponta para o campo `body_bytes` do quadro `i`, não para o primeiro byte do `body`.

Regras da v1, todas obrigatórias:

```text
offset[0]     = header_size + frame_count * 4
offset[i + 1] = offset[i] + 4 + body_bytes[i]
```

Os offsets crescem. Cada passo soma pelo menos 16, porque `body_bytes` é no mínimo 12.

## Quadro

```text
u32 body_bytes
u8  body[body_bytes]
```

`body_bytes` conta só o `body`. O `u32` fica no arquivo e não entra nessa conta. Um leitor recusa `body_bytes` menor que 12 ou maior que 58244.

O `body` é exatamente o que um sender coloca no ar, fatiado como na seção [No ar](#no-ar). O `body_bytes` não vai no datagrama.

## Body

```text
u16 flags
u16 band_count              0..30
u32 band_mask               bit i → a band i segue; bits 30..31 são 0
[u16 palette[256]]          só se o bit 0 de flags está ligado (512 bytes)
band_count records          índice de band crescente
u32 crc32                   de todo byte do body antes deste campo
```

O preâmbulo (`flags`, `band_count`, `band_mask`) tem 8 bytes. O `crc32` tem 4. Sem palette e sem records, o body tem 12 bytes.

### `flags`

| Bit | Nome | Significado |
| ---: | --- | --- |
| 0 | palette present | os 512 bytes da palette seguem o `band_mask` |
| 1 | keyframe | quadro completo, palette presente, 30 bands |

Qualquer outro bit é 0 na v1. Um leitor recusa o quadro se `(flags & ~0x3) != 0`.

Consequência: um keyframe tem `flags = 3`. Um quadro que não é keyframe tem `flags = 0`.

### `band_mask` e `band_count`

`band_count` é o population count de `band_mask` (quantidade de bits 1). Os bits 30 e 31 são 0. Os records seguem os bits ligados, do bit 0 para o bit 29. Não há record para bit desligado, e não há buraco na ordem.

Um quadro parado é legal como **delta**: `flags = 0`, `band_count = 0`, `band_mask = 0`, e em seguida o `crc32`. O player não mexe na GRAM.

### Palette

Presente só no keyframe. São 256 `u16` RGB565 little-endian, sempre 512 bytes, mesmo quando a imagem usa menos cores. Slots não usados podem ser 0. Slots usados não repetem o mesmo RGB565.

A palette de um keyframe vale para todos os deltas até o próximo keyframe. Um delta não carrega palette. Trocar a palette exige um keyframe novo: índices da palette anterior apontariam para outras cores.

Arquivo que preenche a borda com o índice 0 (`contain`) grava `palette[0] = 0` (preto). O header não tem um bit de `fit`. Quem só lê o arquivo não distingue borda preta de uma imagem que também usa o preto no slot 0.

### Record de band

```text
u8  codec
u8  zero                 sempre 0
u16 comp_bytes           tamanho de data, sem o pad
u8  data[comp_bytes]
u8  pad[(4 - (comp_bytes & 3)) & 3]
```

`zero` diferente de 0 invalida o quadro. O pad é só zeros. O record inteiro (cabeçalho de 4 bytes + `data` + pad) tem tamanho múltiplo de 4. O pad não entra em `comp_bytes`.

| `comp_bytes & 3` | pad |
| ---: | ---: |
| 0 | 0 |
| 1 | 3 |
| 2 | 2 |
| 3 | 1 |

Exemplo: `comp_bytes = 30` pede 2 bytes de pad. O record ocupa `4 + 30 + 2 = 36` bytes.

| `codec` | `data` | `comp_bytes` |
| ---: | --- | --- |
| 0 | índices crus | exatamente 1920 |
| 1 | RLE da band | 1..1919 |
| 2 | um bloco LZ4 | 1..1919 |

`codec` acima de 2 invalida o quadro. Codec 0 com tamanho diferente de 1920 invalida. Codec 1 ou 2 com tamanho 0 ou ≥ 1920 invalida. Cada `data` decodifica para exatamente 1920 bytes.

## RLE (codec 1)

Decodificar até a saída ter 1920 bytes. `out` começa em 0. Ler um `u8 ctl`:

| `ctl` | Ação |
| --- | --- |
| 0..127 | literal. `n = ctl + 1` (1..128). Copiar os próximos `n` bytes |
| 128..255 | repetição. `n = ctl - 127` (1..128). Ler um `u8 v`. Escrever `v`, `n` vezes |

`out` tem que terminar em 1920. Saída curta, saída longa, ou byte de entrada sobrando depois disso, é band inválida.

Band de 1920 bytes com o índice 7: `1920 / 128 = 15`, então quinze pares `FF 07`. `ctl = 255` dá `n = 128`. `comp_bytes = 30`.

## LZ4 (codec 2)

Um bloco LZ4, sem frame header, sem content size, sem checksum e sem dictionary. `LZ4_decompress_safe(data, dst, comp_bytes, 1920)` tem que retornar 1920.

Sequência, enquanto ainda houver entrada:

```text
u8  token
    lit_len   = token >> 4
    se lit_len == 15:
        somar bytes extras; cada 255 continua; o byte < 255 encerra a soma
    copiar lit_len literais da entrada para a saída
    se a entrada acabou: fim do bloco          (última sequência não tem match)

u16 offset                                 little-endian, mínimo 1
    offset tem que ser ≥ 1 e ≤ bytes já escritos
    match_len = (token & 15) + 4               mínimo 4
    se (token & 15) == 15:
        somar bytes extras, a mesma regra dos literais
    copiar match_len bytes de dst[out - offset]
    a cópia é byte a byte: offset < match_len é válido e repete o trecho
```

A última sequência do bloco termina depois dos literais. Não há `offset` nem match nessa sequência. Match que escreva além de 1920, offset 0, offset para trás do início da saída, ou entrada cortada no meio de um campo, invalida a band.

## CRC32

O campo `crc32` é o CRC do PNG/zlib de todos os bytes do `body` antes dele. Não cobre `body_bytes`, não cobre outros quadros.

| Parâmetro | Valor |
| --- | --- |
| Polinômio refletido | `0xEDB88320` |
| Init | `0xFFFFFFFF` |
| Xor final | `0xFFFFFFFF` |

```text
crc = 0xFFFFFFFF
para cada byte b do intervalo:
    crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8)
crc = crc ^ 0xFFFFFFFF
```

A tabela é a padrão do CRC-32 refletido. O resíduo é gravado little-endian.

Vetores:

| Entrada | `crc32` |
| --- | --- |
| ASCII `123456789` (9 bytes) | `0xCBF43926` |
| 8 bytes 0 (preâmbulo de um delta vazio) | `0x6522DF69` |

O body de um delta vazio, 12 bytes, é:

```text
00 00  00 00  00 00 00 00  69 DF 22 65
```

Esse body só é legal num quadro que **não** é keyframe. O quadro 0 nunca é um delta.

## Keyframe e delta

```text
gop = max(1, (1000000 + frame_us / 2) / frame_us)
```

O quadro 0 é keyframe. Todo quadro cujo índice é múltiplo de `gop` também é. Isso é um keyframe por segundo: 24 quando `frame_us` é 41667, 30 quando é 33333.

| fps | `frame_us` | `gop` |
| ---: | ---: | ---: |
| 1 | 1000000 | 1 |
| 24 | 41667 | 24 |
| 30 | 33333 | 30 |
| 60 | 16667 | 60 |

Um **keyframe** tem, ao mesmo tempo:

- `flags = 3` (palette e keyframe)
- `band_count = 30`
- `band_mask = 0x3FFFFFFF`
- palette de 256 entradas
- um record para cada band, da 0 à 29

Um **delta** tem `flags = 0`. O bit `i` está ligado se, e só se, os 1920 índices da band `i` diferem do quadro anterior já decodificado. Band igual é omitida. O player cola as bands decodificadas no quadro indexado que já tem.

Depois de decodificar um delta, a comparação com o quadro anterior tem que bater com a máscara: bit ligado significa diferença, bit desligado significa os mesmos 1920 bytes. Um arquivo que marca band limpa, ou esconde band suja, é inválido.

## Tamanho de um keyframe cru

Um keyframe em que as 30 bands usam codec 0:

```text
preâmbulo          8
palette          512
30 records    30 * 1924 = 57720     (4 + 1920 + 0 de pad)
crc32              4
body_bytes     58244
```

Um delta cru, sem palette, com as 30 bands sujas:

```text
8 + 57720 + 4 = 57732
```

58244 é o máximo. Um leitor recusa qualquer body maior, inclusive um keyframe que alguém tente estender.

## Áudio

`audio_offset` 0 significa que o arquivo não tem PCM e acaba no fim do último body. Qualquer outro valor é o offset absoluto do bloco abaixo, e tem que ser o primeiro byte depois desse body.

```text
u32 pcm_bytes          > 0 e par
u32 sample_rate        16000, 24000 ou 48000
u16 channels           1
u16 sample_format      0 = s16le
u32 crc32              dos bytes de PCM, o mesmo CRC do body
s16le pcm[pcm_bytes]
```

O `flags` do header continua 0. O CRC de cada body de vídeo não cobre o PCM. Não há ADPCM nem Opus nesta versão.

A amostra 0 é o instante do quadro 0. A duração do PCM é a do vídeo:

```text
timeline_us  = frame_count * frame_us
sample_count = (timeline_us * sample_rate + 500000) / 1000000
pcm_bytes    = sample_count * 2
```

Origem mais curta termina em silêncio. Origem mais longa é cortada.

## O que um escritor grava

A saída é sempre 240×240 índices. Escala e quantização acontecem antes dos bytes acima.

- `cover`: recorta o centro até um quadrado e reamostra para 240×240.
- `contain`: cabe dentro de 240×240 e preenche o resto com o índice 0. `palette[0]` é RGB565 0.
- No máximo 256 cores, já no RGB565 da fórmula. Uma palette por GOP, e todos os quadros desse GOP mapeados por ela.
- Por band, tentar RLE e LZ4. Descartar resultado com `comp_bytes` ≥ 1920. Gravar o menor. Empate fica com RLE. Se nenhum sobrar, gravar codec 0.

Dither não faz parte do formato. Ele só muda quais índices saem sujos.

O PCM é mono s16le a 16000, 24000 ou 48000 Hz. A contagem de amostras segue a fórmula da seção [Áudio](#áudio). Sem stream de áudio na origem, `audio_offset` fica 0.

## O que um leitor aceita

Recusar o arquivo no primeiro item que falhar:

- Magic, `version`, `header_size`, `width`, `height`, `band_rows`, `band_count_max` e `flags` iguais à tabela. `frame_count ≥ 1`. `frame_us` em `16667 .. 1000000`.
- `offset[0] = header_size + frame_count * 4`, e `offset[i + 1] = offset[i] + 4 + body_bytes[i]`. Com `audio_offset` 0, o arquivo acaba no fim do último body. Com outro valor, esse valor é o fim do último body e o bloco de áudio começa aí.
- Cada `body_bytes` está em `12 .. 58244`.
- O `crc32` confere. Bits de `flags` fora de 0 e 1 estão limpos. Bits 30 e 31 de `band_mask` estão limpos. Population count igual a `band_count`.
- O quadro 0, e todo múltiplo de `gop`, é keyframe: `flags = 3`, 30 bands, máscara `0x3FFFFFFF`, palette presente. Os outros quadros têm `flags = 0`.
- Cada band decodifica para 1920 bytes. Codec 0 tem 1920 bytes crus. Codecs 1 e 2 são mais curtos que 1920. O byte `zero` e o pad são 0. Não sobra byte entre o último record e o `crc32`.
- Percorrendo do quadro 0, cada bit ligado de um delta é uma diferença real contra o quadro indexado anterior, e cada bit desligado é uma band igual.
- Se há áudio: `pcm_bytes` par e maior que 0, `sample_rate` numa das três taxas, `channels` 1, `sample_format` 0, `pcm_bytes` igual à fórmula, CRC do PCM confere, e não sobra byte depois dele.

## O que um player faz com um body válido

1. Se o quadro é keyframe, substituir o quadro indexado de 57600 bytes e guardar a palette.
2. Se é delta, copiar só as bands presentes para o quadro indexado que já está na memória. A palette continua a do último keyframe.
3. Expandir cada índice pela palette para RGB565 só na hora de apresentar. Isso não está no arquivo.
4. Um delta vazio não apresenta de novo.

Um buraco de `frame_index` no transporte descarta deltas até chegar um keyframe. A última imagem apresentada permanece.

O PCM toca no ES8311, no mesmo relógio: a amostra `i` está em `(i * 1000000) / sample_rate` microssegundos, e o quadro `n` em `n * frame_us`. Cerca de 20–40 ms de folga entre os dois basta para este alto-falante. O core 0 recebe o Wi-Fi e alimenta o I2S. O core 1 expande e faz o SPI, e não espera socket. O ring de I2S fica na DRAM interna, dezenas de ms (buffer de 50–100 ms), não segundos. O PA do GPIO7 fica alto só enquanto há áudio, e sobe cerca de 35 ms antes da primeira amostra. ES7210 e AEC duplex ficam de fora. Um quadro de vídeo pode cair. Num buraco de áudio, o player repete ou esmaece a última amostra em vez de travar o present.

## No ar

O arquivo não contém datagramas. Um sender lê os bodies em ordem e fatia cada um. Um datagrama tem no máximo 1472 bytes.

| Offset | Tipo | Campo |
| ---: | --- | --- |
| 0 | u32 | `seq`. Sobe 1 a cada datagrama, inclusive de um quadro para o outro |
| 4 | u32 | `frame_index`. Índice do quadro no arquivo, desde 0 |
| 8 | u16 | `frag_index`. 0 .. `frag_count - 1` |
| 10 | u16 | `frag_count` |
| 12 | u16 | `payload_bytes`. 1..1456 |
| 14 | u16 | reserved. 0 |
| 16 | u8[] | o próximo pedaço do **body** |

```text
frag_count    = ceil(body_bytes / 1456)
payload       = até 1456 bytes do body, em ordem, sem o u32 body_bytes
```

`seq` fica no offset 0 para um contador no estilo de um flood UDP enxergar o buraco. Fragmento faltando derruba o quadro inteiro. `frame_index` mais velho que o último apresentado chega tarde e é descartado. O loop de present não espera um buraco fechar. Não há type byte na frente desse datagrama.

O PCM não entra nesses datagramas. Ele sai noutro socket UDP, com a própria sequência. Um datagrama de áudio também tem no máximo 1472 bytes:

| Offset | Tipo | Campo |
| ---: | --- | --- |
| 0 | u32 | `seq`. Sobe 1 só entre datagramas de áudio |
| 4 | u32 | `time_us`. Instante da primeira amostra deste payload |
| 8 | u16 | `payload_bytes`. Par, 2..1456 |
| 10 | u16 | reserved. 0 |
| 12 | u32 | reserved. 0 |
| 16 | s16le | amostras |

O cabeçalho tem 16 bytes, como o de vídeo. A fatia é o maior payload par que cabe em 1456. 20 ms a 48 kHz mono são 1920 bytes e não cabem. O `seq` de áudio não se mistura com o de vídeo.

A conta de taxa usa o payload UDP, não os 57600 bytes do quadro indexado. Headers IP e UDP ficam de fora; o header de 16 bytes entra, no vídeo e no áudio:

```text
frags     = ceil(payload / 1456)
udp_bytes = payload + 16 * frags
```

| Payload | `frags` | `udp_bytes` |
| --- | ---: | ---: |
| keyframe cru, 58244 | 41 | 58900 |
| delta cru, 57732 | 40 | 58372 |
| 1 s de PCM a 48000 Hz, 96000 | 66 | 97056 |

Um segundo alinhado em `frame_us` soma o `udp_bytes` dos quadros cujo instante `n * frame_us` cai nesse segundo, mais o PCM cuja amostra `i` cai nele. O segundo da amostra é `((i * 1000000) / sample_rate) / 1000000`. A média é `total_udp_bytes * 8 / (frame_count * frame_us)` em Mbit/s, com o áudio dentro do total.

Um arquivo todo sujo, com um keyframe por segundo e sem áudio, fica em cerca de 11,2 Mbit/s a 24 fps e 14,0 Mbit/s a 30 fps. Um segundo de PCM mono a 48 kHz soma cerca de 0,78 Mbit/s. Abaixo de 16 o rádio medido deste painel fica confortável. De 16 a 21 é intermitente. Acima de 21 passa do platô medido.

A sessão em volta desses datagramas (hello, credit, control, pause) está em [api-w154.md](api-w154.md). Ela não altera os bytes do arquivo nem o header de vídeo ou de áudio.
