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

`w154stream.bat` na raiz abre outra console, encerra um `w154stream` que já esteja aberto e sobe o Release gerado por `build.bat --api`. O processo anterior, e a console que o lançou, são mortos com `-Force` antes de trocar o hotspot. Sem o `-Force`, o `finally` da console velha derruba o AP novo. Matar só o `w154stream.exe` tem o mesmo efeito. O bat já elevado alcança um exe que um shell comum não mata (`Access is denied`).

O `Start-Process -Verb RunAs` do Windows ignora o diretório pedido. A console elevada nasce em `C:\WINDOWS\system32`, e `--dir` sem flag fica essa pasta: o log mostra `dir C:\WINDOWS\system32` e `GET /files` não vê o filme. Passe `--dir`, ou um `POST /play` com caminho absoluto.

O hotspot 2.4 GHz usa o `WIFI_SSID` de `firmware/w154/main/wifi_secrets.h` e uma senha UUID nova a cada lançamento. O script grava essa senha e o gateway ICS no header antes de o exe escutar. Se o lançamento morre depois disso, o `finally` desliga o hotspot. O painel continua com a senha do último `flash.bat` que compilou, a string dentro de `w154.bin`, não a do header depois da falha. `flash.bat`, com esse hotspot ligado, regrava o header a partir da instância em execução. Fechar a console do bat, ou Ctrl+C nela, derruba o hotspot. `--http` e `--hello` são os mesmos desta página.

`dotnet run` não sobe nem segura o hotspot. O AP pode cair com a API ainda escutando. Religar usa a senha que já está em `wifi_secrets.h`. Outro `w154stream.bat` encerra o anterior e gera outro UUID. O firmware já gravado não entra nessa rede. Matar o monitor serial pode deixar a COM ocupada (`semaphore timeout` ou `Access is denied`) mesmo sem processo na porta; a gravação seguinte, com a porta livre, segue. Light sleep é outro caso: a porta some, e o Windows mostra um USB de VID `0000` com falha de descriptor. O `flash.bat` procura `VID_303A` e `(COM…)`. O PWR acorda o chip.

Publicar nativo:

```text
dotnet publish W154Stream -c Release
```

`--selftest` grava um `.w154` mínimo, fatia os bodies e confere `seq`, `frag_index` e o corte de 1456. Não abre socket.

## HTTP

Sem auth. Um arquivo por vez. O `POST /play` repete esse arquivo e não manda `eos`. `frameSent` passa de `frame_count`: o `frame_index` no fio não volta a 0, porque o present só mostra `shown + 1`. Um índice recomeçado em 0 nunca entra na tela. O body é o do arquivo em `frame_index % frame_count`, e o PCM usa o mesmo resto nas amostras. O quadro 0 do arquivo é keyframe, então a emenda do loop decodifica. `u32` nesse índice dura anos a 24 fps.

| Método | Caminho | Corpo |
| --- | --- | --- |
| GET | `/files` | lista `name`, `frameCount`, `frameUs`, `hasAudio` |
| POST | `/play` | `{ "name": "clip.w154" }` ou `{ "name": "F:\\clips\\clip.w154" }` |
| POST | `/pause` | vazio |
| POST | `/resume` | vazio |
| GET | `/status` | arquivo, `joined`, `frameSent`, `freeBytes`, `paused`, `underrun`, `fileToken` |

`name` sem raiz de unidade é só o arquivo dentro de `--dir`. Separador de pasta ou `..` responde 400. Um caminho absoluto (`Path.IsPathFullyQualified`) de um `.w154` que exista em qualquer pasta do PC também vale. `C:clip.w154`, sem a barra, não é absoluto.

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5090/play -ContentType "application/json" -Body '{"name":"F:\\clips\\clip.w154"}'
```

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

Um hello com outro IP ou outras portas substitui o client e, se já houver arquivo armado, pede `reset`. O mesmo IP e as mesmas portas não zeram o cursor. Gravar o firmware de novo exige outro `POST /play` para o device receber `reset`. O STA muda de endereço entre associações.

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

16 bytes. Magic ASCII `C154` (`43 31 35 34`). O device manda de novo depois de cada rajada de vídeo e, no máximo, a cada 10 ms. Um crédito só no timer de 20 ms deixa a janela de 20 datagramas parada nesse intervalo, cerca de 1,45 MB/s. O ring de ~8 MB fica vazio (`rx slots 0`), o vídeo cai para 15–18 fps e cada underrun desliga o I2S, então o áudio picota com `gap` 0.

| Offset | Tipo | Campo |
| ---: | --- | --- |
| 0 | u32 | magic |
| 4 | u32 | `free_bytes` |
| 8 | u32 | `expect_seq` |
| 12 | u32 | `play_frame` nos bits 0..29, bit 30 underrun, bit 31 paused |

`expect_seq` é o próximo `seq` de vídeo que o device ainda não contabilizou. A API guarda o total de bytes de body já enviados antes de cada `seq`. O que passou de `expect_seq` ainda está no ar ou por chegar, e sai de `free_bytes`:

```text
outstanding = sent_body - body_before(expect_seq)
no_ar       = outstanding + payload <= min(free_bytes, 20 * 1456)
abre_body   = outstanding + frag_count * 1456 <= free_bytes
```

`no_ar` é o teto do socket de 32 KiB. `abre_body` é a reserva contígua que o device pede ao abrir o slot, e não entra nesse teto: um quadro maior que 29 KiB nunca sairia. O device compara a reserva com a cauda livre, não com os bytes reais do último fragmento.

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

`reset` zera ring, `seq` e relógio. `audio` sai logo em seguida, com o mesmo `file_token`. O `POST /play` repete o arquivo sem parar. O `frame_index` do datagrama continua subindo; o body é o do quadro `frame_index % frame_count`. O PCM faz o mesmo com o offset no arquivo. A API não manda `eos` nesse loop. O device ainda trata `eos` se chegar: toca o que resta no ring e segura o último `frame`, sem tratar o fim como underrun.

Opcode desconhecido ou `file_token` velho é ignorado.

## Vídeo e áudio no ar

Iguais ao arquivo. Vídeo: `seq`, `frame_index`, `frag_index`, `frag_count`, `payload_bytes` 1..1456, reserved 0, depois um pedaço do body. `seq` sobe 1 em todo datagrama de vídeo, atravessando quadros. Áudio: `seq` próprio, `time_us` da primeira amostra, `payload_bytes` par 2..1456.

O host não dorme entre fragmentos do mesmo `frame`. Só segura quando o crédito não cabe o próximo payload. Um body novo só começa se `free_bytes` cobre `frag_count * 1456`, que é o que o device reserva ao abrir o slot. Sem isso o ring cheio descarta o quadro e os deltas até o próximo keyframe. Em cada volta o PCM sai antes do vídeo. No máximo 20 datagramas de vídeo de 1456 bytes ficam no ar ao mesmo tempo: o `SO_RCVBUF` do socket de vídeo no device é 32 KiB, e uma rajada maior perde UDP.

O PCM anda pelo playhead, não pelo ring. O device ignora `time_us` e enfileira na ordem. Cada volta manda no máximo 8 datagramas de áudio, para a rajada caber no `SO_RCVBUF` de 32 KiB. O índice da amostra é absoluto e o byte lido é `amostra % amostras_do_arquivo`.

```text
alvo_us = play_frame * frame_us + 350000
```

A API manda as amostras cujo instante cai até `alvo_us`. No pause do operador ela para o PCM, e o vídeo segue enquanto houver `free_bytes`. O `free_bytes` tem de caber o fragmento que termina o body em curso. Um crédito de algumas centenas de bytes com o quadro pela metade trava o envio; o firmware anuncia a cauda inteira nesse caso.
