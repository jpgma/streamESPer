#include <stdlib.h>
#include <string.h>

#include "audio.h"
#include "backlight.h"
#include "board_pins.h"
#include "decode.h"
#include "driver/gpio.h"
#include "driver/i2c_master.h"
#include "driver/spi_master.h"
#include "esp_err.h"
#include "esp_heap_caps.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_vendor.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "net.h"
#include "present.h"
#include "ring.h"
#include "state.h"

static const char *TAG = "w154";

shared_t g;
frame_slot_t g_slot[2];

static void flags_or(uint32_t bit)
{
    atomic_fetch_or(&g.flags, bit);
}

static void flags_and(uint32_t mask)
{
    atomic_fetch_and(&g.flags, mask);
}

void player_on_reset(uint32_t token, uint32_t frame_us)
{
    if (frame_us < 16667 || frame_us > 1000000) {
        frame_us = 41667;
    }
    atomic_store(&g.token, token);
    atomic_store(&g.frame_us, frame_us);
    atomic_store(&g.sample_rate, 0);
    atomic_store(&g.play_frame, 0);
    atomic_store(&g.flags, 0);
    atomic_store(&g.media_us, 0);
    atomic_store(&g.clock_run, 0);
    atomic_store(&g_slot[0].ready, 0);
    atomic_store(&g_slot[1].ready, 0);
    atomic_store(&g.reading, -1);
    ring_clear();
    atomic_fetch_add(&g.epoch, 1);
    ESP_LOGI(TAG, "reset token %u frame_us %u", (unsigned)token, (unsigned)frame_us);
}

void player_on_pause(void)
{
    flags_or(PF_PAUSE);
}

void player_on_resume(void)
{
    flags_and(~PF_PAUSE);
}

void player_on_audio(uint32_t sample_rate)
{
    if (sample_rate != 0 && sample_rate != 16000 && sample_rate != 24000 && sample_rate != 48000) {
        return;
    }
    atomic_store(&g.sample_rate, sample_rate);
}

void player_on_eos(void)
{
    flags_or(PF_EOS);
}

static void player_boot(void)
{
    decode_init();
    size_t largest = heap_caps_get_largest_free_block(MALLOC_CAP_SPIRAM);
    size_t fixed = (size_t)INDEX_BYTES * 2u + (size_t)ASM_BYTES;
    if (largest < fixed + 256u * 1024u) {
        ESP_LOGE(TAG, "PSRAM livre %u, precisa de mais", (unsigned)largest);
        abort();
    }
    size_t ring_bytes = largest - fixed - 128u * 1024u;
    uint8_t *block = heap_caps_malloc(fixed + ring_bytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (!block) {
        ESP_LOGE(TAG, "carve da PSRAM falhou");
        abort();
    }
    g_slot[0].index = block;
    g_slot[1].index = block + INDEX_BYTES;
    uint8_t *scratch = block + (size_t)INDEX_BYTES * 2u;
    uint8_t *base = scratch + ASM_BYTES;
    ring_mount(base, (uint32_t)ring_bytes, scratch);
    atomic_store(&g.reading, -1);
    ESP_LOGI(TAG, "ring %u bytes", (unsigned)ring_bytes);
}

static void panel_init(esp_lcd_panel_handle_t *panel, esp_lcd_panel_io_handle_t *io)
{
    gpio_config_t bat = {
        .pin_bit_mask = 1ull << PIN_BAT_EN,
        .mode = GPIO_MODE_OUTPUT,
    };
    ESP_ERROR_CHECK(gpio_config(&bat));
    gpio_set_level(PIN_BAT_EN, 1);

    spi_bus_config_t buscfg = {
        .mosi_io_num = PIN_LCD_MOSI,
        .miso_io_num = PIN_LCD_MISO,
        .sclk_io_num = PIN_LCD_CLK,
        .quadwp_io_num = -1,
        .quadhd_io_num = -1,
        .data4_io_num = -1,
        .data5_io_num = -1,
        .data6_io_num = -1,
        .data7_io_num = -1,
        .max_transfer_sz = LCD_FRAME_BYTES,
    };
    ESP_ERROR_CHECK(spi_bus_initialize(SPI3_HOST, &buscfg, SPI_DMA_CH_AUTO));

    esp_lcd_panel_io_spi_config_t io_cfg = {
        .cs_gpio_num = PIN_LCD_CS,
        .dc_gpio_num = PIN_LCD_DC,
        .spi_mode = LCD_SPI_MODE,
        .pclk_hz = LCD_PCLK_HZ,
        .trans_queue_depth = 2,
        .lcd_cmd_bits = 8,
        .lcd_param_bits = 8,
    };
    ESP_ERROR_CHECK(esp_lcd_new_panel_io_spi(SPI3_HOST, &io_cfg, io));

    esp_lcd_panel_dev_config_t panel_cfg = {
        .reset_gpio_num = PIN_LCD_RST,
        .rgb_ele_order = LCD_RGB_ELEMENT_ORDER_RGB,
        .data_endian = LCD_RGB_DATA_ENDIAN_LITTLE,
        .bits_per_pixel = LCD_BPP,
    };
    ESP_ERROR_CHECK(esp_lcd_new_panel_st7789(*io, &panel_cfg, panel));
    ESP_ERROR_CHECK(esp_lcd_panel_reset(*panel));
    ESP_ERROR_CHECK(esp_lcd_panel_init(*panel));
    ESP_ERROR_CHECK(esp_lcd_panel_invert_color(*panel, LCD_INVERT_COLOR));
    ESP_ERROR_CHECK(esp_lcd_panel_disp_on_off(*panel, true));
}

void app_main(void)
{
    esp_lcd_panel_handle_t panel = NULL;
    esp_lcd_panel_io_handle_t io = NULL;
    panel_init(&panel, &io);
    present_init(panel, io);
    backlight_start(panel);

    i2c_master_bus_config_t bus_cfg = {
        .i2c_port = I2C_NUM_0,
        .sda_io_num = PIN_I2C_SDA,
        .scl_io_num = PIN_I2C_SCL,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .flags.enable_internal_pullup = 1,
    };
    i2c_master_bus_handle_t bus = NULL;
    ESP_ERROR_CHECK(i2c_new_master_bus(&bus_cfg, &bus));
    audio_init(bus);
    player_boot();
    net_start();

    ESP_ERROR_CHECK(xTaskCreatePinnedToCore(net_task, "w154_rx", 8192, NULL, 8, NULL, 0) == pdPASS ? ESP_OK : ESP_FAIL);
    ESP_ERROR_CHECK(xTaskCreatePinnedToCore(decode_task, "w154_dec", 8192, NULL, 5, NULL, 0) == pdPASS ? ESP_OK : ESP_FAIL);
    ESP_ERROR_CHECK(xTaskCreatePinnedToCore(audio_task, "w154_aud", 4096, NULL, 10, NULL, 0) == pdPASS ? ESP_OK : ESP_FAIL);
    ESP_ERROR_CHECK(xTaskCreatePinnedToCore(present_task, "w154_pic", 4096, NULL, 7, NULL, 1) == pdPASS ? ESP_OK : ESP_FAIL);
}
