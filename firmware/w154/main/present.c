#include "present.h"

#include <string.h>

#include "board_pins.h"
#include "backlight.h"
#include "driver/spi_master.h"
#include "esp_attr.h"
#include "esp_lcd_panel_io_interface.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "ring.h"
#include "state.h"

/* O draw_bitmap enfileira o DMA e o próximo envio espera a fila. Essa espera travava o present. */
typedef struct {
    spi_transaction_t base;
    struct {
        unsigned int dc_gpio_level: 1;
        unsigned int en_trans_done_cb: 1;
    } flags;
} lcd_tx_t;

typedef struct {
    esp_lcd_panel_io_t base;
    spi_device_handle_t spi_dev;
} io_head_t;

static esp_lcd_panel_io_handle_t s_io;
static int s_bounce_slot;
static uint32_t s_epoch;
static int s_started;
static int64_t s_last_wall;
static uint32_t s_shown;
static int s_have_shown;
static int s_filling;
static int s_fps_shown;
static int s_fps_value;
static int s_fps_draw;
static int64_t s_fps_t0;
static uint32_t s_gap;
static uint32_t s_underrun;
static uint32_t s_late_max;
static int s_repaint;
static int s_held;
static int s_link_phase;
static int s_link_logged;
static int s_dark;
static int64_t s_stall_at;

static const char *TAG = "w154";

#define FPS_SCALE 2
#define FPS_DW (5 * FPS_SCALE)
#define FPS_DH (7 * FPS_SCALE)
#define FPS_PITCH (FPS_DW + 2)
#define FPS_COLS (3 * FPS_PITCH)

DMA_ATTR static uint16_t s_bounce[2][BAND_ROWS * SCREEN_WIDTH];

static const uint8_t s_digit[10][7] = {
    {0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E},
    {0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E},
    {0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F},
    {0x0E, 0x11, 0x01, 0x06, 0x01, 0x11, 0x0E},
    {0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02},
    {0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E},
    {0x0E, 0x10, 0x1E, 0x11, 0x11, 0x11, 0x0E},
    {0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08},
    {0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E},
    {0x0E, 0x11, 0x11, 0x0F, 0x01, 0x01, 0x0E},
};

static int row_dirty(const uint8_t *mask, int row)
{
    return (mask[row >> 3] >> (row & 7)) & 1;
}

static void poll_tx(spi_device_handle_t dev, int dc, const void *data, size_t bytes, int keep_cs)
{
    lcd_tx_t tx = {0};
    tx.base.user = s_io;
    tx.base.length = bytes * 8;
    tx.base.tx_buffer = data;
    if (keep_cs) {
        tx.base.flags = SPI_TRANS_CS_KEEP_ACTIVE;
    }
    tx.flags.dc_gpio_level = dc;
    spi_device_polling_transmit(dev, &tx.base);
}

static void ship(int y, int rows, const uint16_t *pixels)
{
    spi_device_handle_t dev = ((io_head_t *)s_io)->spi_dev;
    uint8_t cmd;
    uint8_t arg[4];
    int y1 = y + rows;

    spi_device_acquire_bus(dev, portMAX_DELAY);
    cmd = 0x2A;
    arg[0] = 0;
    arg[1] = 0;
    arg[2] = (SCREEN_WIDTH - 1) >> 8;
    arg[3] = (SCREEN_WIDTH - 1) & 0xFF;
    poll_tx(dev, 0, &cmd, 1, 1);
    poll_tx(dev, 1, arg, 4, 0);
    cmd = 0x2B;
    arg[0] = (uint8_t)(y >> 8);
    arg[1] = (uint8_t)y;
    arg[2] = (uint8_t)((y1 - 1) >> 8);
    arg[3] = (uint8_t)((y1 - 1) & 0xFF);
    poll_tx(dev, 0, &cmd, 1, 1);
    poll_tx(dev, 1, arg, 4, 0);
    cmd = 0x2C;
    poll_tx(dev, 0, &cmd, 1, 1);
    poll_tx(dev, 1, pixels, (size_t)rows * SCREEN_WIDTH * 2, 0);
    spi_device_release_bus(dev);
}

static void overlay_fps(uint16_t *dst, int y0, int rows)
{
    int fps = s_fps_draw;
    if (fps < 0) {
        fps = 0;
    }
    if (fps > 999) {
        fps = 999;
    }
    int x0 = SCREEN_WIDTH - FPS_COLS - 2;
    int y_top = 1;
    int y_bot = 1 + FPS_DH;
    for (int row = 0; row < rows; row++) {
        int y = y0 + row;
        if (y < y_top || y >= y_bot) {
            continue;
        }
        uint16_t *line = dst + row * SCREEN_WIDTH + x0;
        for (int x = 0; x < FPS_COLS; x++) {
            line[x] = 0x0000;
        }
    }
    int n = fps >= 100 ? 3 : fps >= 10 ? 2 : 1;
    int place = FPS_COLS - n * FPS_PITCH;
    int digits[3];
    int count = 0;
    if (n == 3) {
        digits[count++] = fps / 100;
    }
    if (n >= 2) {
        digits[count++] = (fps / 10) % 10;
    }
    digits[count++] = fps % 10;
    for (int d = 0; d < count; d++) {
        int origin = place + d * FPS_PITCH;
        for (int grow = 0; grow < 7; grow++) {
            uint8_t bits = s_digit[digits[d]][grow];
            for (int gcol = 0; gcol < 5; gcol++) {
                if ((bits & (0x10 >> gcol)) == 0) {
                    continue;
                }
                for (int sy = 0; sy < FPS_SCALE; sy++) {
                    int y = y_top + grow * FPS_SCALE + sy;
                    if (y < y0 || y >= y0 + rows) {
                        continue;
                    }
                    uint16_t *line = dst + (y - y0) * SCREEN_WIDTH;
                    for (int sx = 0; sx < FPS_SCALE; sx++) {
                        line[x0 + origin + gcol * FPS_SCALE + sx] = 0xFFFF;
                    }
                }
            }
        }
    }
}

static void expand_rows(const frame_slot_t *frame, int y0, int rows)
{
    uint16_t *dst = s_bounce[s_bounce_slot];
    for (int row = 0; row < rows; row++) {
        const uint8_t *src = frame->index + (y0 + row) * SCREEN_WIDTH;
        uint16_t *out = dst + row * SCREEN_WIDTH;
        for (int x = 0; x < SCREEN_WIDTH; x++) {
            out[x] = frame->palette[src[x]];
        }
    }
    if (s_fps_draw > 0 && y0 < 16) {
        overlay_fps(dst, y0, rows);
    }
    ship(y0, rows, dst);
    s_bounce_slot ^= 1;
}

static void present_frame(const frame_slot_t *frame)
{
    int y = 0;
    while (y < SCREEN_HEIGHT) {
        int show = s_repaint || y < 16 || row_dirty(frame->rowmask, y);
        if (!show) {
            y++;
            continue;
        }
        int rows = 1;
        while (y + rows < SCREEN_HEIGHT && rows < BAND_ROWS) {
            int next = s_repaint || (y + rows) < 16 || row_dirty(frame->rowmask, y + rows);
            if (!next) {
                break;
            }
            rows++;
        }
        expand_rows(frame, y, rows);
        y += rows;
    }
    s_repaint = 0;
}

/* Círculo com barra no centro. Fora do traço o buffer fica 0x0000, preto no painel. */
static void paint_link(int show)
{
    const int scale = 4;
    const int cells = 16;
    const int span = cells * scale;
    const int x0 = (SCREEN_WIDTH - span) / 2;
    const int y0 = (SCREEN_HEIGHT - span) / 2;
    for (int band = 0; band < BAND_COUNT; band++) {
        uint16_t *dst = s_bounce[s_bounce_slot];
        memset(dst, 0, sizeof(s_bounce[0]));
        if (show) {
            int y_band = band * BAND_ROWS;
            for (int row = 0; row < BAND_ROWS; row++) {
                int y = y_band + row;
                if (y < y0 || y >= y0 + span) {
                    continue;
                }
                int gy = (y - y0) / scale;
                int dy = gy - 7;
                uint16_t *line = dst + row * SCREEN_WIDTH;
                for (int gx = 0; gx < cells; gx++) {
                    int dx = gx - 7;
                    int slash = dx - dy;
                    if (slash < 0) {
                        slash = -slash;
                    }
                    int r2 = dx * dx + dy * dy;
                    if (!((r2 >= 26 && r2 <= 46) || (slash <= 1 && r2 <= 52))) {
                        continue;
                    }
                    for (int sx = 0; sx < scale; sx++) {
                        line[x0 + gx * scale + sx] = 0xFFFF;
                    }
                }
            }
        }
        ship(band * BAND_ROWS, BAND_ROWS, dst);
        s_bounce_slot ^= 1;
    }
}

/* [buffer_goal] Cerca de 3 s, sem passar do que um body máximo cabe no ring. */
static int buffer_goal(void)
{
    uint32_t frame_us = atomic_load(&g.frame_us);
    if (frame_us < 12500 || frame_us > 1000000) {
        frame_us = 41667;
    }
    int frames = (int)(3000000u / frame_us);
    if (frames < 24) {
        frames = 24;
    }
    uint32_t cap = ring_capacity();
    int fit = cap < ASM_BYTES ? 24 : (int)(cap / ASM_BYTES);
    if (fit > RING_SLOTS - 32) {
        fit = RING_SLOTS - 32;
    }
    if (fit < 24) {
        fit = 24;
    }
    if (frames > fit) {
        frames = fit;
    }
    return frames;
}

/* [buffer_low] Abaixo disto o relógio para e espera o buffer_goal de novo. */
static int buffer_low(void)
{
    return 8;
}

/* [buffered_frames] Fila do ring mais os slots que o decode já publicou. */
static int buffered_frames(void)
{
    int ready = 0;
    for (int i = 0; i < 2; i++) {
        if (atomic_load(&g_slot[i].ready)) {
            ready++;
        }
    }
    return ready + (int)ring_queued();
}

static void note_fps(void)
{
    s_fps_shown++;
    int64_t now = esp_timer_get_time();
    if (s_fps_t0 == 0) {
        s_fps_t0 = now;
    }
    int display = s_fps_value;
    if (now - s_fps_t0 >= 1000000) {
        s_fps_value = s_fps_shown;
        display = s_fps_value;
        ESP_LOGI(TAG, "pic fps %d shown %u gap %u late %u us underrun %u", display, (unsigned)s_shown,
                 (unsigned)s_gap, (unsigned)s_late_max, (unsigned)s_underrun);
        s_fps_shown = 0;
        s_gap = 0;
        s_underrun = 0;
        s_late_max = 0;
        s_fps_t0 = now;
    } else if (display == 0 && now - s_fps_t0 > 200000) {
        display = (int)((int64_t)s_fps_shown * 1000000 / (now - s_fps_t0));
    }
    if (now - s_fps_t0 > 200000) {
        s_fps_draw = display;
    }
}

static void clock_stop(int underrun)
{
    uint32_t flags = atomic_load(&g.flags);
    if (underrun && (flags & PF_UNDERRUN) == 0) {
        s_underrun++;
    }
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

static void hold_link(void)
{
    if (s_started) {
        clock_stop(0);
    }
    if (!s_link_logged) {
        ESP_LOGW(TAG, "sem enlace");
        s_link_logged = 1;
    }
    s_held = 1;
    atomic_store(&g.link_screen, 1);
    int phase = (int)((esp_timer_get_time() / 500000) & 1);
    if (phase != s_link_phase) {
        s_link_phase = phase;
        paint_link(phase);
    }
}

static void release_link(void)
{
    if (!s_held) {
        return;
    }
    s_held = 0;
    s_stall_at = 0;
    atomic_store(&g.link_screen, 0);
    if (s_link_phase == 1) {
        paint_link(0);
    }
    s_link_phase = -1;
    s_repaint = 1;
    if (s_link_logged) {
        ESP_LOGI(TAG, "enlace");
        s_link_logged = 0;
    }
}

void present_init(esp_lcd_panel_handle_t panel, esp_lcd_panel_io_handle_t io)
{
    (void)panel;
    s_io = io;
    memset(s_bounce, 0, sizeof(s_bounce));
    for (int y = 0; y < SCREEN_HEIGHT; y += BAND_ROWS) {
        ship(y, BAND_ROWS, s_bounce[0]);
    }
    s_bounce_slot = 1;
}

void present_reset(void)
{
    s_epoch = atomic_load(&g.epoch);
    s_started = 0;
    s_have_shown = 0;
    s_filling = 1;
    s_shown = 0;
    s_fps_shown = 0;
    s_fps_value = 0;
    s_fps_draw = 0;
    s_fps_t0 = 0;
    s_gap = 0;
    s_underrun = 0;
    s_late_max = 0;
    s_repaint = 0;
    s_link_phase = -1;
    s_stall_at = 0;
    s_held = 0;
    s_link_logged = 0;
    atomic_store(&g.link_screen, 0);
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
        if (!backlight_lit()) {
            if (s_started) {
                clock_stop(0);
            }
            s_dark = 1;
            vTaskDelay(pdMS_TO_TICKS(20));
            continue;
        }
        if (s_dark) {
            s_dark = 0;
            s_repaint = 1;
        }

        if (!atomic_load(&g.have_ip)) {
            hold_link();
            vTaskDelay(pdMS_TO_TICKS(20));
            continue;
        }

        int depth = buffered_frames();
        if (s_filling) {
            if (depth < buffer_goal()) {
                if (s_started) {
                    clock_stop(1);
                }
                if (depth == 0 && s_have_shown) {
                    if (s_stall_at == 0) {
                        s_stall_at = esp_timer_get_time();
                    }
                    if (esp_timer_get_time() - s_stall_at >= 300000) {
                        hold_link();
                    }
                } else {
                    s_stall_at = 0;
                    if (s_held) {
                        release_link();
                    }
                }
                vTaskDelay(pdMS_TO_TICKS(20));
                continue;
            }
            s_filling = 0;
            s_stall_at = 0;
        } else if (depth < buffer_low()) {
            s_filling = 1;
            if (s_started) {
                clock_stop(1);
            }
            vTaskDelay(pdMS_TO_TICKS(20));
            continue;
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
        uint32_t later = UINT32_MAX;
        for (int i = 0; i < 2; i++) {
            if (!atomic_load(&g_slot[i].ready)) {
                continue;
            }
            uint32_t frame = g_slot[i].frame;
            if (frame == want) {
                found = i;
                break;
            }
            if (frame > s_shown && frame < later) {
                found = i;
                later = frame;
            }
        }
        if (found < 0) {
            int eos = (atomic_load(&g.flags) & PF_EOS) != 0;
            if (!eos && s_have_shown) {
                clock_stop(1);
                if (s_stall_at == 0) {
                    s_stall_at = esp_timer_get_time();
                }
            } else {
                s_stall_at = 0;
            }
            if (s_stall_at != 0 && esp_timer_get_time() - s_stall_at >= 300000) {
                hold_link();
            }
            vTaskDelay(pdMS_TO_TICKS(20));
            continue;
        }
        s_stall_at = 0;
        release_link();

        if (!s_started) {
            clock_start();
        }
        uint32_t frame_us = atomic_load(&g.frame_us);
        if (frame_us == 0) {
            frame_us = 41667;
        }
        uint32_t show = g_slot[found].frame;
        if (s_have_shown && show > s_shown + 1) {
            uint32_t hole = show - s_shown - 1;
            s_gap += hole;
            ESP_LOGW(TAG, "frame %u salto de %u", (unsigned)show, (unsigned)hole);
            atomic_store(&g.media_us, (int64_t)show * frame_us);
            s_last_wall = esp_timer_get_time();
        } else if (!s_have_shown) {
            atomic_store(&g.media_us, (int64_t)show * frame_us);
            s_last_wall = esp_timer_get_time();
        }
        int64_t now = esp_timer_get_time();
        int64_t media = atomic_load(&g.media_us) + (now - s_last_wall);
        s_last_wall = now;
        atomic_store(&g.media_us, media);
        int64_t due = (int64_t)show * frame_us;
        int64_t remain = due - media;
        if (remain > 1500) {
            int ms = (int)((remain - 1500) / 1000);
            if (ms >= 10) {
                vTaskDelay(pdMS_TO_TICKS(ms));
            } else {
                int64_t until = esp_timer_get_time() + remain;
                while (esp_timer_get_time() < until) {
                }
            }
            continue;
        }
        if (remain > 0) {
            int64_t until = esp_timer_get_time() + remain;
            while (esp_timer_get_time() < until) {
            }
        } else if (-remain > s_late_max) {
            s_late_max = (uint32_t)(-remain);
        }

        atomic_store(&g.reading, found);
        note_fps();
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
