# API de streaming `.w154`

A Minimal API entrega um arquivo `.w154` ao painel. O operador escolhe o arquivo por HTTP. O device entra com um datagram de hello e passa a receber unicast. O body de vídeo e o PCM seguem a seção [No ar](formato-w154.md#no-ar). Esta página é só a sessão.

Termos de campo ficam em inglês. A reprodução começa no primeiro keyframe que o device monta. O vídeo não espera `frame_us` no host: sai na velocidade do `SendTo`, limitado pelo crédito. O excedente fica num ring na PSRAM do device. O PCM não usa esse ring.

## Processo

```text
dotnet run --project W154Stream -- --dir pasta-de-w154
dotnet run --project W154Stream -- --dir pasta --http 5090 --hello 5091
dotnet run --project W154Stream -- --selftest
```

| Flag | Padrão | Papel |
| --- | --- | --- |
| `--dir` | diretório atual | pasta com `*.w154` no topo, sem subpasta |
| `--http` | 5090 | HTTP do operador |
| `--hello` | 5091 | UDP que recebe hello e credit |

Publicar nativo:

```text
dotnet publish W154Stream -c Release
```

`--selftest` grava um `.w154` mínimo, fatia os bodies e confere `seq`, `frag_index` e o corte de 1456. Não abre socket.

## HTTP

Sem auth. Um arquivo por vez. Toca uma vez e o device segura o último `frame`.

| Método | Caminho | Corpo |
| --- | --- | --- |
| GET | `/files` | lista `name`, `frameCount`, `frameUs`, `hasAudio` |
| POST | `/play` | `{ "name": "clip.w154" }` |
| POST | `/pause` | vazio |
| POST | `/resume` | vazio |
| GET | `/status` | arquivo, `joined`, `frameSent`, `freeBytes`, `paused`, `underrun`, `fileToken` |

`name` é só o nome do arquivo. Separador de pasta ou `..` responde 400.

`POST /play` arma o arquivo e incrementa `fileToken`. Se já houver client, o host manda `reset` e em seguida o `frame` 0. Se ainda não houver hello, o primeiro hello dispara esse `reset`.

`paused` é o pause do operador. `underrun` vem do credit do device.

## Portas

O host escuta o hello em `--hello`. O device escolhe as três portas de recepção e as anuncia no hello. O host manda cada fluxo para o `IP` de origem do hello.

| Fluxo | Direção | Conteúdo |
| --- | --- | --- |
| hello / credit | device → host | sessão |
| vídeo | host → `video_port` | body fatiado, sem type byte |
| áudio | host → `audio_port` | PCM |
| control | host → `control_port` | reset, pause, resume, áudio, eos |

Um hello novo substitui o client anterior e zera o cursor de envio.

## Hello

16 bytes, little-endian. Magic ASCII `H154` (`48 31 35 34`).

| Offset | Tipo | Campo |
| ---: | --- | --- |
| 0 | u32 | magic |
| 4 | u16 | `video_port` |
| 6 | u16 | `audio_port` |
| 8 | u16 | `control_port` |
| 10 | u16 | reserved, 0 |
| 12 | u32 | `ring_bytes`, capacidade do ring de bodies |

## Credit

16 bytes. Magic ASCII `C154` (`43 31 35 34`). O device manda de novo a cada 20 ms e ao soltar um body.

| Offset | Tipo | Campo |
| ---: | --- | --- |
| 0 | u32 | magic |
| 4 | u32 | `free_bytes` |
| 8 | u32 | `expect_seq` |
| 12 | u32 | `play_frame` nos bits 0..29, bit 30 underrun, bit 31 paused |

`expect_seq` é o próximo `seq` de vídeo que o device ainda não contabilizou. A API guarda o total de bytes de body já enviados antes de cada `seq`. O que passou de `expect_seq` ainda está no ar ou por chegar, e sai de `free_bytes`:

```text
outstanding = sent_body - body_before(expect_seq)
pode_enviar = outstanding + payload <= free_bytes
```

`play_frame` é o quadro que o relógio do device está mostrando. Bit 30 ligado: o próximo body ainda não chegou e o relógio pausou. Bit 31 ligado: pause do operador, já aplicado no device. Os dois param o consumo, e o ring continua recebendo até `free_bytes` ir a zero.

## Control

16 bytes. Magic ASCII `K154` (`4B 31 35 34`).

| Offset | Tipo | Campo |
| ---: | --- | --- |
| 0 | u32 | magic |
| 4 | u32 | opcode |
| 8 | u32 | `file_token` |
| 12 | u32 | arg |

| Opcode | Nome | arg |
| ---: | --- | --- |
| 1 | reset | `frame_us` |
| 2 | pause | 0 |
| 3 | resume | 0 |
| 4 | audio | `sample_rate`, ou 0 se o arquivo não tem PCM |
| 5 | eos | `frame_count` |

`reset` zera ring, `seq` e relógio. `audio` sai logo em seguida, com o mesmo `file_token`. `eos` sai quando o último fragmento de vídeo e o PCM que cabe na duração já foram enviados. O device toca o que ainda está no ring e segura o último `frame`, sem tratar o fim como underrun.

Opcode desconhecido ou `file_token` velho é ignorado.

## Vídeo e áudio no ar

Iguais ao arquivo. Vídeo: `seq`, `frame_index`, `frag_index`, `frag_count`, `payload_bytes` 1..1456, reserved 0, depois um pedaço do body. `seq` sobe 1 em todo datagrama de vídeo, atravessando quadros. Áudio: `seq` próprio, `time_us` da primeira amostra, `payload_bytes` par 2..1456.

O host não dorme entre fragmentos do mesmo `frame`. Só segura quando o crédito não cabe o próximo payload.

O PCM anda pelo playhead, não pelo ring:

```text
alvo_us = play_frame * frame_us + 80000
```

A API manda as amostras cujo instante cai até `alvo_us`. No pause do operador ela para o PCM, e o vídeo segue enquanto houver `free_bytes`.
