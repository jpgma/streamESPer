#include "present.h"

#include <string.h>

#include "board_pins.h"
#include "esp_attr.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "state.h"

static esp_lcd_panel_handle_t s_panel;
static SemaphoreHandle_t s_spi_done;
static int s_inflight;
static int s_bounce_slot;
static uint32_t s_epoch;
static int s_started;
static int64_t s_last_wall;
static uint32_t s_shown;
static int s_have_shown;

DMA_ATTR static uint16_t s_bounce[2][BAND_ROWS * SCREEN_WIDTH];

static bool spi_done(esp_lcd_panel_io_handle_t io, esp_lcd_panel_io_event_data_t *edata, void *ctx)
{
    (void)io;
    (void)edata;
    (void)ctx;
    BaseType_t wake = pdFALSE;
    xSemaphoreGiveFromISR(s_spi_done, &wake);
    return wake == pdTRUE;
}

static int row_dirty(const uint8_t *mask, int row)
{
    return (mask[row >> 3] >> (row & 7)) & 1;
}

static void ship(int y, int rows, const uint16_t *pixels)
{
    if (s_inflight == 2) {
        xSemaphoreTake(s_spi_done, portMAX_DELAY);
        s_inflight--;
    }
    esp_lcd_panel_draw_bitmap(s_panel, 0, y, SCREEN_WIDTH, y + rows, pixels);
    s_inflight++;
}

static void expand_band(const frame_slot_t *frame, int band)
{
    uint16_t *dst = s_bounce[s_bounce_slot];
    int y0 = band * BAND_ROWS;
    for (int row = 0; row < BAND_ROWS; row++) {
        const uint8_t *src = frame->index + (y0 + row) * SCREEN_WIDTH;
        uint16_t *out = dst + row * SCREEN_WIDTH;
        for (int x = 0; x < SCREEN_WIDTH; x++) {
            out[x] = frame->palette[src[x]];
        }
    }
    ship(y0, BAND_ROWS, dst);
    s_bounce_slot ^= 1;
}

static void present_frame(const frame_slot_t *frame)
{
    int any = 0;
    for (int band = 0; band < BAND_COUNT; band++) {
        if (!row_dirty(frame->rowmask, band * BAND_ROWS)) {
            continue;
        }
        any = 1;
        expand_band(frame, band);
    }
    if (!any) {
        return;
    }
    while (s_inflight > 0) {
        if (xSemaphoreTake(s_spi_done, pdMS_TO_TICKS(50)) == pdTRUE) {
            s_inflight--;
        } else {
            break;
        }
    }
}

static void clock_stop(int underrun)
{
    uint32_t flags = atomic_load(&g.flags);
    if (underrun) {
        flags |= PF_UNDERRUN;
    }
    atomic_store(&g.flags, flags);
    atomic_store(&g.clock_run, 0);
    s_started = 0;
}

static void clock_start(void)
{
    uint32_t flags = atomic_load(&g.flags);
    flags &= ~PF_UNDERRUN;
    atomic_store(&g.flags, flags);
    s_last_wall = esp_timer_get_time();
    s_started = 1;
    atomic_store(&g.clock_run, 1);
}

void present_init(esp_lcd_panel_handle_t panel, esp_lcd_panel_io_handle_t io)
{
    s_panel = panel;
    s_spi_done = xSemaphoreCreateBinary();
    esp_lcd_panel_io_callbacks_t cbs = {
        .on_color_trans_done = spi_done,
    };
    esp_lcd_panel_io_register_event_callbacks(io, &cbs, NULL);
    memset(s_bounce, 0, sizeof(s_bounce));
    for (int y = 0; y < SCREEN_HEIGHT; y += BAND_ROWS) {
        ship(y, BAND_ROWS, s_bounce[0]);
    }
    while (s_inflight > 0 && xSemaphoreTake(s_spi_done, pdMS_TO_TICKS(50)) == pdTRUE) {
        s_inflight--;
    }
}

void present_reset(void)
{
    s_epoch = atomic_load(&g.epoch);
    s_started = 0;
    s_have_shown = 0;
    s_shown = 0;
    atomic_store(&g.media_us, 0);
    atomic_store(&g.clock_run, 0);
    atomic_store(&g.reading, -1);
}

void present_task(void *arg)
{
    (void)arg;
    present_reset();
    for (;;) {
        uint32_t epoch = atomic_load(&g.epoch);
        if (epoch != s_epoch) {
            present_reset();
            atomic_store(&g_slot[0].ready, 0);
            atomic_store(&g_slot[1].ready, 0);
        }
        uint32_t flags = atomic_load(&g.flags);
        if (flags & PF_PAUSE) {
            if (s_started) {
                clock_stop(0);
            }
            vTaskDelay(pdMS_TO_TICKS(5));
            continue;
        }

        int found = -1;
        uint32_t want = s_have_shown ? s_shown + 1 : 0;
        for (int i = 0; i < 2; i++) {
            if (atomic_load(&g_slot[i].ready) && g_slot[i].frame == want) {
                found = i;
                break;
            }
        }
        if (found < 0 && s_have_shown) {
            for (int i = 0; i < 2; i++) {
                if (!atomic_load(&g_slot[i].ready) || g_slot[i].frame <= s_shown) {
                    continue;
                }
                if (g_slot[i].frame == want) {
                    found = i;
                    break;
                }
            }
        }
        if (found < 0) {
            int eos = (atomic_load(&g.flags) & PF_EOS) != 0;
            if (!eos && s_have_shown) {
                clock_stop(1);
            }
            vTaskDelay(pdMS_TO_TICKS(1));
            continue;
        }

        if (!s_started) {
            clock_start();
        }
        uint32_t frame_us = atomic_load(&g.frame_us);
        if (frame_us == 0) {
            frame_us = 41667;
        }
        int64_t now = esp_timer_get_time();
        int64_t media = atomic_load(&g.media_us) + (now - s_last_wall);
        s_last_wall = now;
        atomic_store(&g.media_us, media);
        if (s_have_shown && (uint64_t)media < (uint64_t)g_slot[found].frame * frame_us) {
            atomic_store(&g.media_us, media);
            vTaskDelay(pdMS_TO_TICKS(1));
            continue;
        }

        atomic_store(&g.reading, found);
        present_frame(&g_slot[found]);
        s_shown = g_slot[found].frame;
        s_have_shown = 1;
        atomic_store(&g.play_frame, s_shown);
        atomic_store(&g_slot[found].ready, 0);
        atomic_store(&g.reading, -1);
        uint32_t live = atomic_load(&g.flags);
        live &= ~PF_UNDERRUN;
        atomic_store(&g.flags, live);
    }
}
