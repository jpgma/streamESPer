# Firmware do player `.w154`

App ESP-IDF em `firmware/w154`, para o ESP32-S3 do painel Waveshare 1,54". Não depende do `flash.bat` do outro repositório. Os pinos usados estão em `firmware/w154/main/board_pins.h`, copiados do `board.h` daquele painel.

```text
copy firmware\w154\main\wifi_secrets.example.h firmware\w154\main\wifi_secrets.h
flash.bat
flash.bat --port COM7
flash.bat COM7
```

`wifi_secrets.h` traz `WIFI_SSID`, `WIFI_PASS`, `HOST_IP` e `HOST_HELLO_PORT` (5091). A API lê o SSID desse arquivo. Cada `w154stream.bat` gera um UUID novo e usa esse valor como senha WPA2 do hotspot, gravando-o no header. Com o hotspot no ar, `flash.bat` na raiz chama `firmware/flash.ps1`, que relê o SSID, essa senha e o gateway dessa instância e regrava o header antes do `idf.py`. `HOST_IP` fica o endereço ICS, em geral `192.168.137.1`. O ESP-IDF esperado é o 5.5, em `%USERPROFILE%\esp\esp-idf` ou `IDF_PATH`.

O arquivo e os datagramas de vídeo e PCM estão em [formato-w154.md](formato-w154.md). A sessão (hello, credit, control) está em [api-w154.md](api-w154.md).

## Tasks

| Task | Core | Função |
| --- | --- | --- |
| Wi-Fi | 0 | STA, modem sleep desligado |
| `rx` | 0 | três sockets, montagem do body, credit |
| `decode` | 0 | RLE, LZ4, CRC, quadro indexado |
| `audio` | 0 | ring de I2S e PA |
| `present` | 1 | expand e SPI |
| `bl` | 0 | fade do backlight e light sleep |

O core 1 não lê socket. O core 0 não espera o DMA do painel. Prioridade: Wi-Fi 23, áudio 10, `rx` 8, present 7, decode 5, backlight 1. O áudio fica acima do decode para o codec não perder o prazo enquanto um quadro é expandido.

## Memória

Carve único no boot. Nada de `malloc` no caminho do `frame`.

| Buffer | Onde | Tamanho |
| --- | --- | --- |
| Dois quadros indexados | PSRAM | 57600 cada |
| Scratch de decode | PSRAM | 58244 |
| Ring de bodies | PSRAM | maior bloco livre, menos uma folga |
| Tabela de slots | DRAM interna | 512 entradas |
| Bounce duplo | DRAM interna, `DMA_ATTR` | `2 × 8 × 240 × 2` = 7680 |
| Row mask | DRAM interna | 30 bytes por slot |
| Ring de PCM | DRAM interna | 400 ms a 48 kHz |

`free_bytes` do credit é a cauda livre do ring, o bastante para o próximo datagrama e para terminar o body que já começou. A tabela guarda `offset`, `length` e `frame_index` de cada body já completo. Um body incompleto não entra nessa tabela: `rx slots` pode ser 0 com um parcial no fim do buffer.

Fila vazia anuncia a capacidade inteira. O próximo body recomeça no offset 0, então o resto no fim do buffer não é espaço livre. Anunciar só esse resto (na casa de 1400 bytes) faz o host parar para sempre. Descontar do crédito os bytes que ainda faltam no parcial tem o mesmo efeito: o credit chegou a 252 bytes com o quadro pela metade, o host não mandou o fragmento seguinte e a imagem congelou. Se a cauda não cabe o resto, o parcial é descartado e o quadro vira salto.

O socket de vídeo usa `SO_RCVBUF` de 32 KiB. A API não deixa mais de 20 datagramas de 1456 (~29 KiB) no ar. Rajada maior perde UDP, o `expect_seq` fura e o device trata como perda. Aumentar essa janela não enche o ring: o que falta é crédito logo depois da rajada, no máximo a cada 10 ms. O socket de áudio tem o mesmo 32 KiB. 350 ms a 48 kHz mono s16 são cerca de 33 KiB; uma rajada só estoura o buffer e o fill nasce baixo. O host manda no máximo 8 datagramas de áudio por volta.

`begin_slot` reserva `frag_count * 1456` contíguos ao abrir o body. O último fragmento quase nunca enche 1456, então a reserva passa dos bytes reais. Sem esse espaço o pacote é recusado, `skip_key` liga e os deltas caem até o próximo keyframe. O log é `video seq N esperava N`: o `seq` batia com `expect_seq`, o slot é que não abriu. Um buraco de UDP mostra dois números diferentes. O salto na imagem fica na casa de um GOP e o `pic fps` continua perto de 24, porque o relógio avança até o keyframe que sobrou. A tabela tem 512 entradas; neste painel o byte cap de 7953020 limita a fila a cerca de 170 bodies.

## Relógio

`t0` é o instante em que o relógio solta, não o do primeiro keyframe. O present espera cerca de 3 s de bodies (entre 24 e 72 quadros) antes de mostrar o primeiro. Essa conta é `ring_queued` mais os dois slots que o decode já publicou. `ring_diag` zera o contador de salto e não serve aqui. Os dois slots não são o preroll: o decode esvazia o ring assim que um slot libera, então a folga tem de ficar na PSRAM. O `frame` `n` é devido em `n * frame_us`. Se a fila cai abaixo de 8 quadros, o relógio para e só volta quando os 3 s enchem de novo. O `media_us` fica congelado, então a volta não adianta os quadros que já eram devidos. Enquanto chega dado, essa espera não abre a tela de enlace. A tela de enlace continua só quando a fila fica em 0 por 300 ms, ou quando não há IP.

Underrun: o próximo body ainda não está no ring e não houve buraco de `seq`. O relógio do vídeo pausa, o bit 30 do credit sobe, e o `rx` continua. O canal I2S é desligado nesse instante. O DMA são 8 blocos de 480 amostras, cerca de 80 ms a 48 kHz; se ele seguir, a imagem parada ainda soa. O PA fica ligado nesse buraco curto. Quando a fila volta ao patamar de 3 s, o relógio retoma de onde parou, salvo pause do operador.

`play_frame` só avança quando um quadro é mostrado. Com o crédito em dia, um arquivo a 24 fps e PCM 48 kHz segura `pic fps` 24, `gap` 0, `late` abaixo de 1 ms e `aud fill` perto de 350 ms com `underrun` 0. O ring permanece cheio enquanto o enlace aguenta o realtime; `free_bytes` fica baixo de propósito. `rx slots 0` com o arquivo armado é fila vazia, não steady state. `vTaskDelay(1)` neste tick de 100 Hz dura cerca de 10 ms; espera menor que isso no present é spin.

Perda: `seq` furou. O `frame` em montagem é descartado, e os deltas seguintes também, até um body com o bit de keyframe. A última imagem fica no painel.

`eos`: o host avisou o fim. O que resta no ring toca, e o último `frame` fica parado sem bit de underrun.

Pause do operador: o mesmo congelamento do relógio, com o bit 31. O socket não fecha. O ring enche até `free_bytes` zerar.

Delta vazio não chama SPI. Band com bit desligado não entra no row mask. O bit `i` do `band_mask` liga as linhas `[8i, 8i+8)` no mask de 30 bytes.

## Present

O slot publicado tem o quadro indexado, a palette e o row mask. O core 1 expande cada band suja para o bounce e envia a faixa por `spi_device_polling_transmit`: CASET `0x2A`, RASET `0x2B`, RAMWR `0x2C`, com o callback de DC do driver. O bounce fica na DRAM interna (`DMA_ATTR`).

`esp_lcd_panel_draw_bitmap` enfileira o DMA e o comando seguinte espera o anterior dentro do driver. Uma contagem própria de transferências em voo, com espera bloqueante no fim do quadro, travou o present depois de cerca de 10–12 s a 24 fps: o log de `pic` para, os slots do ring sobem e o áudio entra em underrun porque `play_frame` não anda.

As duas primeiras bands levam o número de fps no canto superior direito, só os dígitos, fonte 5×7 em escala 2. A palette do arquivo é a cor de antes do invert. Com `LCD_INVERT_COLOR`, o pixel `0x0000` sai preto no vidro e `0xFFFF` sai branco. O fundo da marca de desconexão é `0x0000`. O painel é ST7789, SPI modo 3 a 80 MHz. O USB do módulo anuncia VID `303A`. O STA pega DHCP no hotspot; o endereço muda entre associações.

Sem IP, ou sem o próximo quadro por mais de 300 ms, o relógio para, o PA desce e o quadro deixa de ir ao painel. A tela fica preta e um círculo cortado pisca no centro, meio segundo aceso e meio apagado, para não gravar uma imagem parada. O I2S para no mesmo instante, para não sobrar áudio no DMA. Ao voltar o quadro, a tela inteira é redesenhada.

O botão PWR (GPIO5) faz o fade do backlight no GPIO46, 420 ms entre aceso e apagado. Com o vidro apagado o relógio e o PA param. 30 s depois o rádio desce e o chip entra em light sleep até o PWR baixar de novo. O USB-JTAG desce junto. O Windows deixa de ver `VID_303A` e passa a mostrar `Unknown USB Device (Device Descriptor Request Failed)`, VID `0000`. `flash.bat` responde que não há porta Espressif. Isso não é a COM ocupada pelo monitor: não há porta para passar em `-Port`. O PWR acorda o chip. Na volta o painel é reiniciado sem pulsar o RST, o Wi-Fi sobe e o backlight acende.

## Áudio

ES8311 em slave, MCLK = 256 × `sample_rate`, Philips 16-bit estéreo com L = R. Taxas 16000, 24000 e 48000. O device ignora `time_us` e enfileira o PCM na ordem de chegada. O PA do GPIO7 sobe cerca de 35 ms antes da primeira amostra e desce no pause, no reset, sem IP, com o backlight apagado ou com a marca de desconexão. Num underrun curto o PA fica, e o canal I2S é desligado junto com o relógio. Buraco de `seq` de áudio repete a última amostra. ES7210 não é inicializado.

O host mantém o PCM ~350 ms à frente do `play_frame`. O ring guarda 400 ms a 48 kHz, o bastante para o jitter do Wi-Fi, e não o arquivo inteiro. O I2S só começa depois de ~200 ms no ring e com o relógio do vídeo andando. Uma linha por segundo no monitor resume fill, underrun, descarte e buraco de `seq`.
