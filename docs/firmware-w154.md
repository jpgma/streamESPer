# Firmware do player `.w154`

App ESP-IDF em `firmware/w154`, para o ESP32-S3 do painel Waveshare 1,54". Não depende do `flash.bat` do outro repositório. Os pinos usados estão em `firmware/w154/main/board_pins.h`, copiados do `board.h` daquele painel.

```text
copy firmware\w154\main\wifi_secrets.example.h firmware\w154\main\wifi_secrets.h
powershell -File firmware\flash.ps1
powershell -File firmware\flash.ps1 -Port COM7
```

`wifi_secrets.h` traz `WIFI_SSID`, `WIFI_PASS`, `HOST_IP` e `HOST_HELLO_PORT` (5091). O ESP-IDF esperado é o 5.5, em `%USERPROFILE%\esp\esp-idf` ou `IDF_PATH`.

O arquivo e os datagramas de vídeo e PCM estão em [formato-w154.md](formato-w154.md). A sessão (hello, credit, control) está em [api-w154.md](api-w154.md).

## Tasks

| Task | Core | Função |
| --- | --- | --- |
| Wi-Fi | 0 | STA, modem sleep desligado |
| `rx` | 0 | três sockets, montagem do body, credit |
| `decode` | 0 | RLE, LZ4, CRC, quadro indexado |
| `audio` | 0 | ring de I2S e PA |
| `present` | 1 | expand e SPI |

O core 1 não lê socket. O core 0 não espera o DMA do painel.

## Memória

Carve único no boot. Nada de `malloc` no caminho do `frame`.

| Buffer | Onde | Tamanho |
| --- | --- | --- |
| Dois quadros indexados | PSRAM | 57600 cada |
| Scratch de decode | PSRAM | 58244 |
| Ring de bodies | PSRAM | maior bloco livre, menos uma folga |
| Tabela de slots | DRAM interna | 128 entradas |
| Bounce duplo | DRAM interna, `DMA_ATTR` | `2 × 8 × 240 × 2` = 7680 |
| Row mask | DRAM interna | 30 bytes por slot |
| Ring de PCM | DRAM interna | ~100 ms da taxa do arquivo |

`free_bytes` do credit é o que ainda cabe no ring de bodies. A tabela guarda `offset`, `length` e `frame_index` de cada body já completo.

## Relógio

`t0` é o instante em que o primeiro keyframe fica pronto. O `frame` `n` é devido em `n * frame_us`. Não há espera para encher a PSRAM.

Underrun: o próximo body ainda não está no ring e não houve buraco de `seq`. O relógio e o I2S pausam, o bit 30 do credit sobe, e o `rx` continua. Quando o body chega, o relógio volta, salvo pause do operador.

Perda: `seq` furou. O `frame` em montagem é descartado, e os deltas seguintes também, até um body com o bit de keyframe. A última imagem fica no painel.

`eos`: o host avisou o fim. O que resta no ring toca, e o último `frame` fica parado sem bit de underrun.

Pause do operador: o mesmo congelamento do relógio, com o bit 31. O socket não fecha. O ring enche até `free_bytes` zerar.

Delta vazio não chama SPI. Band com bit desligado não entra no row mask. O bit `i` do `band_mask` liga as linhas `[8i, 8i+8)` no mask de 30 bytes.

## Present

O slot publicado tem o quadro indexado, a palette e o row mask. O core 1 expande cada band suja para o bounce ocioso e chama `esp_lcd_panel_draw_bitmap` enquanto o outro bounce ainda está no DMA. A palette do arquivo é a cor de antes do invert; o painel inverte na inicialização.

## Áudio

ES8311 em slave, MCLK = 256 × `sample_rate`, Philips 16-bit estéreo com L = R. Taxas 16000, 24000 e 48000. O PA do GPIO7 sobe cerca de 35 ms antes da primeira amostra e desce quando o PCM para. Buraco curto repete a última amostra. ES7210 não é inicializado.

O host mantém o PCM ~80 ms à frente do `play_frame`. Este ring só absorve esse prazo, não o arquivo inteiro.
