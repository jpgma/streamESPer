#pragma once

/* Pinos do Waveshare ESP32-S3-Touch-LCD-1.54, copiados de board.h do repo do painel. */

#define SCREEN_WIDTH 240
#define SCREEN_HEIGHT 240
#define LCD_BPP 16
#define LCD_FRAME_BYTES (SCREEN_WIDTH * SCREEN_HEIGHT * (LCD_BPP / 8))

#define PIN_LCD_CS 21
#define PIN_LCD_CLK 38
#define PIN_LCD_MOSI 39
#define PIN_LCD_MISO (-1)
#define PIN_LCD_DC 45
#define PIN_LCD_RST 40
#define PIN_LCD_BL 46

#define LCD_SPI_MODE 3
#define LCD_PCLK_HZ (80 * 1000 * 1000)
#define LCD_INVERT_COLOR 1

#define PIN_I2C_SCL 41
#define PIN_I2C_SDA 42
#define ES8311_ADDR 0x18

#define PIN_PA 7
#define PIN_I2S_MCLK 8
#define PIN_I2S_BCLK 9
#define PIN_I2S_WS 10
#define PIN_I2S_DOUT 12

#define PIN_BAT_EN 2
