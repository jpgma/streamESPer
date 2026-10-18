#include "audio.h"

#include <string.h>

#include "backlight.h"
#include "board_pins.h"
#include "driver/gpio.h"
#include "driver/i2s_std.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "state.h"

static const char *TAG = "w154";

/* 400 ms a 48 kHz. O host fica ~350 ms à frente; o I2S só consome com o relógio do vídeo. */
#define AUDIO_CAP (48000 * 4 / 10)

static i2c_master_dev_handle_t s_codec;
static i2s_chan_handle_t s_tx;
static int16_t s_ring[AUDIO_CAP];
static int s_count;
static int s_read;
static int16_t s_last;
static int s_pa;
static int s_tx_on;
static int s_primed;
static int64_t s_pa_at;
static uint32_t s_rate;
static uint32_t s_epoch;
static int s_started;
static uint32_t s_underrun;
static uint32_t s_drop;
static uint32_t s_gap;
static int64_t s_log_at;
static portMUX_TYPE s_mux = portMUX_INITIALIZER_UNLOCKED;

static esp_err_t wr(uint8_t reg, uint8_t val)
{
    uint8_t buf[2] = {reg, val};
    return i2c_master_transmit(s_codec, buf, 2, 100);
}

/* Slave, MCLK = 256*fs. A mesma sequência do hello, que acompanha a taxa do I2S. */
static esp_err_t codec_on(void)
{
    static const uint8_t seq[][2] = {
        {0x44, 0x08}, {0x44, 0x08}, {0x01, 0x30}, {0x02, 0x00}, {0x03, 0x10}, {0x16, 0x24},
        {0x04, 0x10}, {0x05, 0x00}, {0x0B, 0x00}, {0x0C, 0x00}, {0x10, 0x1F}, {0x11, 0x7F},
        {0x00, 0x80}, {0x01, 0x3F}, {0x02, 0x00}, {0x05, 0x00}, {0x03, 0x10}, {0x04, 0x20},
        {0x07, 0x00}, {0x08, 0xFF}, {0x06, 0x03}, {0x09, 0x0C}, {0x0A, 0x0C}, {0x13, 0x10},
        {0x1B, 0x0A}, {0x1C, 0x6A}, {0x44, 0x58}, {0x17, 0xBF}, {0x0E, 0x02}, {0x12, 0x00},
        {0x14, 0x1A}, {0x0D, 0x01}, {0x15, 0x40}, {0x37, 0x08}, {0x45, 0x00}, {0x31, 0x00},
        {0x32, 0xB0},
    };
    for (size_t i = 0; i < sizeof(seq) / sizeof(seq[0]); i++) {
        esp_err_t err = wr(seq[i][0], seq[i][1]);
        if (err != ESP_OK) {
            return err;
        }
    }
    return ESP_OK;
}

static void i2s_stop(void)
{
    if (s_tx && s_tx_on) {
        i2s_channel_disable(s_tx);
        s_tx_on = 0;
    }
}

static void i2s_open(uint32_t rate)
{
    if (s_tx) {
        i2s_stop();
        i2s_del_channel(s_tx);
        s_tx = NULL;
    }
    i2s_chan_config_t chan = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_0, I2S_ROLE_MASTER);
    chan.dma_desc_num = 8;
    chan.dma_frame_num = 480;
    ESP_ERROR_CHECK(i2s_new_channel(&chan, &s_tx, NULL));
    i2s_std_config_t std = {
        .clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(rate),
        .slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(I2S_DATA_BIT_WIDTH_16BIT, I2S_SLOT_MODE_STEREO),
        .gpio_cfg = {
            .mclk = PIN_I2S_MCLK,
            .bclk = PIN_I2S_BCLK,
            .ws = PIN_I2S_WS,
            .dout = PIN_I2S_DOUT,
            .din = I2S_GPIO_UNUSED,
        },
    };
    std.clk_cfg.mclk_multiple = I2S_MCLK_MULTIPLE_256;
    ESP_ERROR_CHECK(i2s_channel_init_std_mode(s_tx, &std));
    s_rate = rate;
}

void audio_init(i2c_master_bus_handle_t bus)
{
    gpio_config_t pa = {
        .pin_bit_mask = 1ull << PIN_PA,
        .mode = GPIO_MODE_OUTPUT,
    };
    ESP_ERROR_CHECK(gpio_config(&pa));
    gpio_set_level(PIN_PA, 0);
    i2c_device_config_t cfg = {
        .device_address = ES8311_ADDR,
        .scl_speed_hz = 100000,
    };
    ESP_ERROR_CHECK(i2c_master_bus_add_device(bus, &cfg, &s_codec));
}

void audio_reset(void)
{
    taskENTER_CRITICAL(&s_mux);
    s_count = 0;
    s_read = 0;
    s_last = 0;
    s_primed = 0;
    taskEXIT_CRITICAL(&s_mux);
    s_epoch = atomic_load(&g.epoch);
    s_started = 0;
    s_underrun = 0;
    s_drop = 0;
    s_gap = 0;
    if (s_pa) {
        gpio_set_level(PIN_PA, 0);
        s_pa = 0;
    }
    i2s_stop();
}

void audio_set_rate(uint32_t sample_rate)
{
    if (sample_rate != 16000 && sample_rate != 24000 && sample_rate != 48000) {
        audio_reset();
        s_rate = 0;
        return;
    }
    if (sample_rate != s_rate) {
        i2s_open(sample_rate);
        ESP_LOGI(TAG, "ES8311 %u Hz", (unsigned)sample_rate);
    }
}

void audio_push(const uint8_t *pcm, uint16_t bytes)
{
    if ((bytes & 1u) != 0 || s_rate == 0) {
        return;
    }
    int samples = bytes / 2;
    int off = 0;
    while (off < samples) {
        int n = samples - off;
        if (n > 128) {
            n = 128;
        }
        int16_t tmp[128];
        for (int i = 0; i < n; i++) {
            const uint8_t *p = pcm + (off + i) * 2;
            tmp[i] = (int16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
        }
        taskENTER_CRITICAL(&s_mux);
        int room = AUDIO_CAP - s_count;
        int take = n < room ? n : room;
        for (int i = 0; i < take; i++) {
            s_ring[(s_read + s_count) % AUDIO_CAP] = tmp[i];
            s_count++;
        }
        s_drop += (uint32_t)(n - take);
        taskEXIT_CRITICAL(&s_mux);
        if (take < n) {
            break;
        }
        off += n;
    }
}

void audio_note_gap(void)
{
    taskENTER_CRITICAL(&s_mux);
    s_gap++;
    taskEXIT_CRITICAL(&s_mux);
}

static int pull(int16_t *dst, int want)
{
    int got = 0;
    taskENTER_CRITICAL(&s_mux);
    while (got < want && s_count > 0) {
        dst[got++] = s_ring[s_read];
        s_last = s_ring[s_read];
        s_read = (s_read + 1) % AUDIO_CAP;
        s_count--;
    }
    taskEXIT_CRITICAL(&s_mux);
    if (got < want) {
        s_underrun += (uint32_t)(want - got);
        while (got < want) {
            dst[got++] = s_last;
        }
    }
    return got;
}

static void audio_log(uint32_t fill)
{
    int64_t now = esp_timer_get_time();
    if (s_log_at != 0 && now - s_log_at < 1000000) {
        return;
    }
    s_log_at = now;
    taskENTER_CRITICAL(&s_mux);
    uint32_t drop = s_drop;
    uint32_t gap = s_gap;
    s_drop = 0;
    s_gap = 0;
    taskEXIT_CRITICAL(&s_mux);
    uint32_t underrun = s_underrun;
    s_underrun = 0;
    if (!s_started && underrun == 0 && drop == 0 && gap == 0) {
        return;
    }
    unsigned ms = s_rate ? (unsigned)(fill * 1000u / s_rate) : 0;
    ESP_LOGI(TAG, "aud fill %u ms underrun %u drop %u gap %u", ms, (unsigned)underrun, (unsigned)drop, (unsigned)gap);
}

void audio_task(void *arg)
{
    (void)arg;
    int16_t stereo[128 * 2];
    for (;;) {
        uint32_t epoch = atomic_load(&g.epoch);
        if (epoch != s_epoch) {
            audio_reset();
        }
        uint32_t rate = atomic_load(&g.sample_rate);
        if (rate != s_rate) {
            audio_set_rate(rate);
        }
        int clock = atomic_load(&g.clock_run) != 0;
        int paused = (atomic_load(&g.flags) & PF_PAUSE) != 0;
        int linked = atomic_load(&g.have_ip) != 0;
        int lit = backlight_lit();
        int have = 0;
        taskENTER_CRITICAL(&s_mux);
        have = s_count;
        taskEXIT_CRITICAL(&s_mux);
        audio_log((uint32_t)have);
        if (paused || s_rate == 0 || !linked || !lit) {
            if (s_pa) {
                gpio_set_level(PIN_PA, 0);
                s_pa = 0;
                s_primed = 0;
                i2s_stop();
            }
            vTaskDelay(pdMS_TO_TICKS(5));
            continue;
        }
        if (!clock) {
            vTaskDelay(pdMS_TO_TICKS(1));
            continue;
        }
        s_started = 1;
        if (!s_pa) {
            gpio_set_level(PIN_PA, 1);
            s_pa = 1;
            s_pa_at = esp_timer_get_time();
            vTaskDelay(pdMS_TO_TICKS(35));
            if (s_tx && !s_tx_on) {
                i2s_channel_enable(s_tx);
                s_tx_on = 1;
                codec_on();
            }
            continue;
        }
        if (esp_timer_get_time() - s_pa_at < 35000) {
            vTaskDelay(pdMS_TO_TICKS(1));
            continue;
        }
        taskENTER_CRITICAL(&s_mux);
        have = s_count;
        taskEXIT_CRITICAL(&s_mux);
        if (!s_primed && have < (int)(s_rate / 5)) {
            vTaskDelay(pdMS_TO_TICKS(2));
            continue;
        }
        s_primed = 1;
        int16_t mono[128];
        pull(mono, 128);
        for (int i = 0; i < 128; i++) {
            stereo[i * 2] = mono[i];
            stereo[i * 2 + 1] = mono[i];
        }
        if (s_tx) {
            size_t wrote = 0;
            i2s_channel_write(s_tx, stereo, sizeof(stereo), &wrote, pdMS_TO_TICKS(20));
        }
    }
}
