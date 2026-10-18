# Formato `.w154` versão 2

Contrato do arquivo que um conversor grava e que um player ou um sender lê. A versão deste texto é a v2. Um leitor da v2 recusa qualquer outro `version` ou `header_size`. Arquivo da v1 não é lido: a origem é reconvertida.

O painel mostra RGB565. O arquivo guarda **índices de palette**, um record por linha suja. O Wi-Fi carrega o `body` de cada quadro, nunca um quadro RGB565 inteiro. O áudio, quando existe, fica num bloco IMA ADPCM depois do último quadro. O host expande esse bloco para s16le antes do socket de áudio. O datagrama no ar continua PCM.

Nomes de campo, de flag e de estrutura ficam em inglês.

## Convenções

| Tipo | Tamanho | Ordem |
| --- | --- | --- |
| `u8` | 1 | — |
| `i8` | 1 | complemento de dois |
| `u16` / `i16` | 2 | little-endian, byte baixo primeiro |
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
| Linha | 240 índices. O record `i` é a linha `y = i` |
| `palette` | 256 × RGB565 = 512 bytes |
| `row_mask` | 30 bytes, 240 bits. O bit `i` (byte `i >> 3`, bit `i & 7`) marca a linha `i` |

O present do painel ainda pinta no máximo 8 linhas por rajada de SPI. Essa rajada não está alinhada a uma faixa fixa: linhas sujas consecutivas saem juntas, até 8. As linhas 0..15 vão ao vidro mesmo limpas, para os dígitos de fps.

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
audio_offset != 0: bloco de áudio começa nesse byte e o arquivo acaba no fim do ADPCM
```

Não há bytes entre o header e a tabela, nem entre a tabela e o primeiro quadro, nem entre quadros. Com áudio, o bloco começa no byte seguinte ao último body.

Os offsets são `u32`. O arquivo pode passar de 2 GiB e tem de ficar abaixo de 4 GiB. No leitor .NET o comprimento de um `Span` é `int`, então body e ADPCM são endereçados por ponteiro mais o offset.

## Header

| Offset | Tipo | Campo | v2 |
| ---: | --- | --- | --- |
| 0 | char[4] | magic | `W154` (`57 31 35 34`) |
| 4 | u16 | `version` | 2 |
| 6 | u16 | `header_size` | 32 |
| 8 | u16 | `width` | 240 |
| 10 | u16 | `height` | 240 |
| 12 | u16 | `band_rows` | 1 |
| 14 | u16 | `band_count_max` | 240 |
| 16 | u32 | `frame_us` | período de um quadro, em microssegundos |
| 20 | u32 | `frame_count` | ≥ 1 |
| 24 | u32 | `flags` | 0 |
| 28 | u32 | `audio_offset` | 0, ou o offset absoluto do bloco de áudio |

`band_rows` e `band_count_max` ficam no header com esses nomes. Na v2 um record é uma linha: `band_rows` é 1 e `band_count_max` é 240.

`frame_us` aceito por um leitor está em `12500 .. 1000000` (80 fps .. 1 fps), inclusive. Abaixo de 12500 o firmware recusa o reset.

Para um fps inteiro de 1 a 80:

```text
frame_us = (1000000 + fps / 2) / fps
```

| fps | `frame_us` | bytes little-endian |
| ---: | ---: | --- |
| 24 | 41667 | `C3 A2 00 00` |
| 30 | 33333 | `35 82 00 00` |
| 60 | 16667 | `3B 41 00 00` |
| 80 | 12500 | `D4 30 00 00` |
| 1 | 1000000 | `40 42 0F 00` |

O player apresenta o quadro `n` em `n * frame_us` a partir do início, com `n` contado de zero.

Header de um arquivo de 24 fps com um quadro, em hex:

```text
57 31 35 34  02 00  20 00  F0 00 F0 00  01 00 F0 00
C3 A2 00 00  01 00 00 00  00 00 00 00  00 00 00 00
24 00 00 00
```

A última linha é `offset[0]`. `32 + 1 * 4 = 36` (`0x24`). Os quatro zeros do fim da segunda linha são `audio_offset`: esse exemplo não tem áudio.

## Tabela de offsets

Começa no byte `header_size`. São `frame_count` valores `u32`. `offset[i]` aponta para o campo `body_bytes` do quadro `i`, não para o primeiro byte do `body`.

Regras da v2, todas obrigatórias:

```text
offset[0]     = header_size + frame_count * 4
offset[i + 1] = offset[i] + 4 + body_bytes[i]
```

Os offsets crescem. Cada passo soma pelo menos 42, porque `body_bytes` é no mínimo 38.

## Quadro

```text
u32 body_bytes
u8  body[body_bytes]
```

`body_bytes` conta só o `body`. O `u32` fica no arquivo e não entra nessa conta. Um leitor recusa `body_bytes` menor que 38 ou maior que 59110.

O `body` é exatamente o que um sender coloca no ar, fatiado como na seção [No ar](#no-ar). O `body_bytes` não vai no datagrama.

## Body

```text
u16 flags
u16 row_count               0..240
u8  row_mask[30]            bit i → a linha i segue
[u16 palette[256]]          só se o bit 0 de flags está ligado (512 bytes)
row_count records           índice de linha crescente
u32 crc32                   de todo byte do body antes deste campo
```

O preâmbulo (`flags`, `row_count`, `row_mask`) tem 34 bytes. O `crc32` tem 4. Sem palette e sem records, o body tem 38 bytes.

### `flags`

| Bit | Nome | Significado |
| ---: | --- | --- |
| 0 | palette present | os 512 bytes da palette seguem o `row_mask` |
| 1 | keyframe | quadro completo, palette presente, 240 linhas |

Qualquer outro bit é 0 na v2. Um leitor recusa o quadro se `(flags & ~0x3) != 0`.

Consequência: um keyframe tem `flags = 3`. Um quadro que não é keyframe tem `flags = 0`.

### `row_mask` e `row_count`

`row_count` é o population count de `row_mask`. Os records seguem os bits ligados, do bit 0 ao bit 239. Não há record para bit desligado, e não há buraco na ordem.

Um quadro parado é legal como **delta**: `flags = 0`, `row_count = 0`, `row_mask` com 30 zeros, e em seguida o `crc32`. O player não mexe na GRAM, salvo as linhas 0..15 quando os dígitos de fps estão ligados.

### Palette

Presente só no keyframe. São 256 `u16` RGB565 little-endian, sempre 512 bytes, mesmo quando a imagem usa menos cores. Slots não usados podem ser 0. Slots usados não repetem o mesmo RGB565.

A palette de um keyframe vale para todos os deltas até o próximo keyframe. Um delta não carrega palette. Trocar a palette exige um keyframe novo: índices da palette anterior apontariam para outras cores.

Arquivo que preenche a borda com o índice 0 (`contain`) grava `palette[0] = 0` (preto). O header não tem um bit de `fit`. Quem só lê o arquivo não distingue borda preta de uma imagem que também usa o preto no slot 0.

### Record de linha

```text
u8  codec
i8  dx                   deslocamento horizontal; 0 fora do codec 2
u16 comp_bytes           tamanho de data, sem o pad
u8  data[comp_bytes]
u8  pad[(4 - (comp_bytes & 3)) & 3]
```

O pad é só zeros. O record inteiro (cabeçalho de 4 bytes + `data` + pad) tem tamanho múltiplo de 4. O pad não entra em `comp_bytes`.

| `comp_bytes & 3` | pad |
| ---: | ---: |
| 0 | 0 |
| 1 | 3 |
| 2 | 2 |
| 3 | 1 |

| `codec` | `data` | `comp_bytes` | `dx` |
| ---: | --- | --- | --- |
| 0 | índices crus da linha | exatamente 240 | 0 |
| 1 | RLE dos índices absolutos | 1..239 | 0 |
| 2 | RLE do resíduo depois do deslocamento | 1..239 | −16..16, só em delta |
| 3 | RLE do delta com o pixel da esquerda | 1..239 | 0, só em keyframe |

`codec` acima de 3 invalida o quadro. Codec 0 com tamanho diferente de 240 invalida. Codecs 1, 2 e 3 com tamanho 0 ou ≥ 240 invalidam. Codec 2 num keyframe, ou codec 3 num delta, invalida. Cada `data` decodifica para exatamente 240 bytes. O que não fica abaixo de 240 bytes é gravado cru.

## RLE (codecs 1, 2 e 3)

O RLE é o mesmo nos três. Decodificar até a saída ter o comprimento pedido (240 bytes numa linha). `out` começa em 0. Ler um `u8 ctl`:

| `ctl` | Ação |
| --- | --- |
| 0..127 | literal. `n = ctl + 1` (1..128). Copiar os próximos `n` bytes |
| 128..255 | repetição. `n = ctl - 127` (1..128). Ler um `u8 v`. Escrever `v`, `n` vezes |

`out` tem que terminar no comprimento pedido. Saída curta, saída longa, ou byte de entrada sobrando depois disso, invalida o record.

Linha de 240 bytes com o índice 7: um par `FF 07` (128 vezes) e um par `EF 07` (`ctl = 239`, `n = 112`). `comp_bytes = 4`.

## Resíduo e delta à esquerda

Codec 2, só em delta. `dx` é procurado de −16 a 16. O preditor da coluna `x` é o índice da coluna `x - dx` na mesma linha do quadro anterior. Fora de 0..239 a coluna gruda no pixel da borda. O resíduo é `index[x] - pred[x]` em módulo 256, e a soma na hora de aplicar também é módulo 256. O record guarda o RLE desse resíduo, não a linha.

Codec 3, só em keyframe. `out[0] = index[0]`. Para `x > 0`, `out[x] = index[x] - index[x - 1]` em módulo 256. Na leitura, `index[0] = out[0]` e `index[x] = index[x - 1] + out[x]`.

O resíduo lê a linha do quadro anterior, nunca o buffer que está sendo escrito.

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
| 34 bytes 0 (preâmbulo de um delta vazio) | `0x41716C2A` |

O body de um delta vazio, 38 bytes, é 34 zeros seguidos de:

```text
2A 6C 71 41
```

Esse body só é legal num quadro que **não** é keyframe. O quadro 0 nunca é um delta.

## Keyframe e delta

```text
gop = max(1, (1000000 + frame_us / 2) / frame_us)
```

O quadro 0 é keyframe. Todo quadro cujo índice é múltiplo de `gop` também é. Isso é um keyframe por segundo: 24 quando `frame_us` é 41667, 80 quando é 12500.

| fps | `frame_us` | `gop` |
| ---: | ---: | ---: |
| 1 | 1000000 | 1 |
| 24 | 41667 | 24 |
| 30 | 33333 | 30 |
| 60 | 16667 | 60 |
| 80 | 12500 | 80 |

Um **keyframe** tem, ao mesmo tempo:

- `flags = 3` (palette e keyframe)
- `row_count = 240`
- `row_mask` com os 30 bytes em `0xFF`
- palette de 256 entradas
- um record para cada linha, da 0 à 239, sem codec 2

Um **delta** tem `flags = 0`. O bit `i` está ligado se, e só se, os 240 índices da linha `i` diferem do quadro anterior já decodificado. Linha igual é omitida. O player cola as linhas decodificadas no quadro indexado que já tem. Um delta não usa codec 3.

Depois de decodificar um delta, a comparação com o quadro anterior tem que bater com a máscara: bit ligado significa diferença, bit desligado significa os mesmos 240 bytes. Um arquivo que marca linha limpa, ou esconde linha suja, é inválido.

## Tamanho de um keyframe cru

Um keyframe em que as 240 linhas usam codec 0:

```text
preâmbulo           34
palette            512
240 records   240 * 244 = 58560     (4 + 240 + 0 de pad)
crc32                4
body_bytes       59110
```

59110 é o máximo. Um leitor recusa qualquer body maior, inclusive um keyframe que alguém tente estender. Esse valor é `ASM_BYTES` no firmware.

## Áudio

`audio_offset` 0 significa que o arquivo não tem áudio e acaba no fim do último body. Qualquer outro valor é o offset absoluto do bloco abaixo, e tem que ser o primeiro byte depois desse body.

```text
u32 pcm_bytes          > 0 e múltiplo de 4
u32 sample_rate        16000, 24000 ou 48000
u16 channels           1
u16 sample_format      1 = IMA ADPCM mono
u32 crc32              dos bytes de ADPCM, o mesmo CRC do body
u8  adpcm[pcm_bytes]
```

`sample_format` 0 (s16le no arquivo) é recusado. O `flags` do header continua 0. O CRC de cada body de vídeo não cobre o áudio.

A amostra 0 é o instante do quadro 0. A contagem decodificada segue a duração do vídeo:

```text
timeline_us  = frame_count * frame_us
sample_count = (timeline_us * sample_rate + 500000) / 1000000
pcm_bytes    = payload IMA dessa contagem
```

Origem mais curta termina em silêncio. Origem mais longa é cortada. O ffmpeg ainda entrega s16le; o escritor codifica IMA antes de gravar o bloco.

O payload é uma sequência de blocos independentes. Um bloco cheio tem 256 bytes e 505 amostras: a primeira é o preditor exato, e as outras 504 saem de 252 bytes de nibble (nibble baixo primeiro).

```text
i16 predictor          amostra 0 do bloco, little-endian
u8  step_index         0..88 no início do bloco
u8  zero               sempre 0
u8  nibbles[(amostras - 1 + 1) / 2]
u8  pad até múltiplo de 4
```

```text
block_bytes = align4(4 + amostras / 2)
```

505 amostras dão 256 bytes. O último bloco é curto e usa a mesma fórmula. `pcm_bytes` é a soma desses blocos. O índice de passo do encoder atravessa os blocos, mas cada cabeçalho grava o índice daquele bloco, então um bloco decodifica sozinho. A tabela de passo tem as 89 entradas padrão do IMA, e a de índice tem 16 entradas (o sinal repete as oito primeiras).

O datagrama de áudio não carrega esses nibbles. Quem envia decodifica o bloco que contém a amostra pedida e manda s16le. A conta de taxa do rádio usa `sample_count * 2`, o PCM expandido, não `pcm_bytes`.

## O que um escritor grava

A saída é sempre 240×240 índices. Escala e quantização acontecem antes dos bytes acima.

- `cover`: recorta o centro até um quadrado e reamostra para 240×240.
- `contain`: cabe dentro de 240×240 e preenche o resto com o índice 0. `palette[0]` é RGB565 0.
- Até 256 cores no RGB565 da fórmula. Uma palette por GOP. Depois do corte, os slots usados são ordenados por luminância OKLab. `contain` deixa o preto no slot 0. O GOP é mapeado nessa ordem.
- Por linha suja, tentar RLE absoluto, RLE do delta à esquerda no keyframe, e RLE de resíduo com `dx` de −16 a 16 no delta. Descartar resultado com `comp_bytes` ≥ 240. Gravar o menor. Empate fica com o codec tentado antes. Se nenhum sobrar, gravar codec 0.

Dither não faz parte do formato. Ele só muda quais índices saem sujos. O conversor pode segurar o índice anterior quando a cor antiga cabe na margem de erro, e pode descer o teto de cores quando um segundo passaria de 16 Mbit/s. Isso está em [conversor-w154.md](conversor-w154.md).

O áudio no arquivo é IMA ADPCM mono a 16000, 24000 ou 48000 Hz. A contagem de amostras segue a fórmula da seção [Áudio](#áudio). Sem stream de áudio na origem, `audio_offset` fica 0.

## O que um leitor aceita

Recusar o arquivo no primeiro item que falhar:

- Magic, `version`, `header_size`, `width`, `height`, `band_rows`, `band_count_max` e `flags` iguais à tabela. `frame_count ≥ 1`. `frame_us` em `12500 .. 1000000`.
- `offset[0] = header_size + frame_count * 4`, e `offset[i + 1] = offset[i] + 4 + body_bytes[i]`. Com `audio_offset` 0, o arquivo acaba no fim do último body. Com outro valor, esse valor é o fim do último body e o bloco de áudio começa aí.
- Cada `body_bytes` está em `38 .. 59110`.
- O `crc32` confere. Bits de `flags` fora de 0 e 1 estão limpos. Population count igual a `row_count`.
- O quadro 0, e todo múltiplo de `gop`, é keyframe: `flags = 3`, 240 linhas, máscara toda ligada, palette presente, sem codec 2. Os outros quadros têm `flags = 0` e não usam codec 3.
- Cada linha decodifica para 240 bytes. Codec 0 tem 240 bytes crus. Codecs 1, 2 e 3 são mais curtos que 240. O pad é 0. `dx` só sai de zero no codec 2, e fica em −16..16. Não sobra byte entre o último record e o `crc32`.
- Percorrendo do quadro 0, cada bit ligado de um delta é uma diferença real contra o quadro indexado anterior, e cada bit desligado é uma linha igual.
- Se há áudio: `pcm_bytes` múltiplo de 4 e maior que 0, `sample_rate` numa das três taxas, `channels` 1, `sample_format` 1, `pcm_bytes` igual ao payload IMA da contagem de amostras, cada bloco é aceito, CRC do ADPCM confere, e não sobra byte depois dele.

## O que um player faz com um body válido

1. Se o quadro é keyframe, substituir o quadro indexado de 57600 bytes e guardar a palette.
2. Se é delta, copiar só as linhas presentes para o quadro indexado que já está na memória. A palette continua a do último keyframe. O resíduo lê o outro buffer.
3. Expandir cada índice pela palette para RGB565 só na hora de apresentar. Isso não está no arquivo.
4. Um delta vazio não apresenta de novo, salvo as linhas 0..15 quando os dígitos de fps estão ligados.
5. Linhas sujas consecutivas saem em rajadas de no máximo 8. O firmware não decodifica ADPCM.

Um buraco de `frame_index` no transporte descarta deltas até chegar um keyframe. A última imagem apresentada permanece.

O PCM expandido toca no ES8311, no mesmo relógio: a amostra `i` está em `(i * 1000000) / sample_rate` microssegundos, e o quadro `n` em `n * frame_us`. O host segura cerca de 350 ms de PCM à frente de `play_frame`. O device ignora `time_us` e enfileira por `seq`. O ring de I2S fica na DRAM interna, 400 ms a 48 kHz. Vinte a quarenta milissegundos não cobrem o jitter deste enlace: o underrun de vídeo desliga o I2S e o áudio picota. O PA do GPIO7 sobe cerca de 35 ms antes da primeira amostra e desce no pause, no reset, sem IP, com o backlight apagado ou na marca de desconexão. Num underrun curto o PA fica. ES7210 e AEC duplex ficam de fora. Num buraco de áudio o player repete a última amostra. O restante do player está em [firmware-w154.md](firmware-w154.md).

O relógio espera cerca de 3 s de bodies antes do primeiro quadro, sem passar do que cabe no ring: `cap / 59110` bodies de tamanho máximo, e também `512 - 32` slots. A 80 fps esse teto fica perto de 1,7 s. Um stream gordo não espera uma meta que o ring nunca segura.

## No ar

O arquivo não contém datagramas. Um sender lê os bodies em ordem e fatia cada um. Um datagrama tem no máximo 1472 bytes.

| Offset | Tipo | Campo |
| ---: | --- | --- |
| 0 | u32 | `seq`. Sobe 1 a cada datagrama, inclusive de um quadro para o outro |
| 4 | u32 | `frame_index`. Índice da sessão, crescente. O body é o do arquivo em `frame_index % frame_count` |
| 8 | u16 | `frag_index`. 0 .. `frag_count - 1` |
| 10 | u16 | `frag_count` |
| 12 | u16 | `payload_bytes`. 1..1456 |
| 14 | u16 | reserved. 0 |
| 16 | u8[] | o próximo pedaço do **body** |

```text
frag_count    = ceil(body_bytes / 1456)
payload       = até 1456 bytes do body, em ordem, sem o u32 body_bytes
```

`seq` fica no offset 0 para um contador no estilo de um flood UDP enxergar o buraco. Fragmento faltando derruba o quadro inteiro. `frame_index` mais velho que o último apresentado chega tarde e é descartado. O present só mostra `shown + 1` (ou um quadro mais novo, contando o salto). Por isso o índice no fio não volta a 0 no fim do arquivo: a sessão continua, e o body lido é `frame_index % frame_count`. O quadro 0 do arquivo é keyframe, então a emenda decodifica quando o índice da sessão bate com `shown + 1`. O loop de present não espera um buraco fechar. Não há type byte na frente desse datagrama.

O ADPCM não entra nesses datagramas. O host expande o bloco IMA que contém a amostra e manda s16le noutro socket UDP, com a própria sequência. Um datagrama de áudio também tem no máximo 1472 bytes:

| Offset | Tipo | Campo |
| ---: | --- | --- |
| 0 | u32 | `seq`. Sobe 1 só entre datagramas de áudio |
| 4 | u32 | `time_us`. Instante da primeira amostra deste payload |
| 8 | u16 | `payload_bytes`. Par, 2..1456 |
| 10 | u16 | reserved. 0 |
| 12 | u32 | reserved. 0 |
| 16 | s16le | amostras já expandidas |

O cabeçalho tem 16 bytes, como o de vídeo. A fatia é o maior payload par que cabe em 1456. 20 ms a 48 kHz mono são 1920 bytes e não cabem. O `seq` de áudio não se mistura com o de vídeo.

A conta de taxa usa o payload UDP, não os 57600 bytes do quadro indexado. Headers IP e UDP ficam de fora; o header de 16 bytes entra, no vídeo e no áudio. O áudio entra como s16 expandido (`sample_count * 2`), mesmo com ADPCM no arquivo:

```text
frags     = ceil(payload / 1456)
udp_bytes = payload + 16 * frags
```

| Payload | `frags` | `udp_bytes` |
| --- | ---: | ---: |
| keyframe cru, 59110 | 41 | 59766 |
| delta vazio, 38 | 1 | 54 |
| 1 s de PCM s16 a 48000 Hz, 96000 | 66 | 97056 |

Um segundo alinhado em `frame_us` soma o `udp_bytes` dos quadros cujo instante `n * frame_us` cai nesse segundo, mais o PCM expandido cuja amostra `i` cai nele. O segundo da amostra é `((i * 1000000) / sample_rate) / 1000000`. A média é `total_udp_bytes * 8 / (frame_count * frame_us)` em Mbit/s, com o áudio dentro do total.

Abaixo de 16 o rádio medido deste painel fica confortável. De 16 a 21 é intermitente. Acima de 21 passa do platô medido.

A sessão em volta desses datagramas (hello, credit, control, pause) está em [api-w154.md](api-w154.md). Ela não altera os bytes do arquivo nem o header de vídeo ou de áudio.
