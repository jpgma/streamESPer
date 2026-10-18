#include "decode.h"

#include <string.h>

#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "ring.h"
#include "state.h"

static const char *TAG = "w154";
static uint32_t s_crc_table[256];
static int s_have_frame;
static uint32_t s_next_frame;
static uint32_t s_epoch;

static uint32_t crc32_of(const uint8_t *data, uint32_t len)
{
    uint32_t crc = 0xFFFFFFFFu;
    for (uint32_t i = 0; i < len; i++) {
        crc = s_crc_table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
    }
    return crc ^ 0xFFFFFFFFu;
}

static int rle_decode(const uint8_t *src, uint32_t src_len, uint8_t *dst, uint32_t dst_len)
{
    uint32_t ip = 0;
    uint32_t op = 0;
    while (op < dst_len) {
        if (ip >= src_len) {
            return -1;
        }
        uint8_t ctl = src[ip++];
        if (ctl < 128) {
            uint32_t n = (uint32_t)ctl + 1u;
            if (ip + n > src_len || op + n > dst_len) {
                return -1;
            }
            memcpy(dst + op, src + ip, n);
            ip += n;
            op += n;
        } else {
            uint32_t n = (uint32_t)ctl - 127u;
            if (ip >= src_len || op + n > dst_len) {
                return -1;
            }
            memset(dst + op, src[ip++], n);
            op += n;
        }
    }
    return op == dst_len && ip == src_len ? 0 : -1;
}

static int pred_x(int x, int dx)
{
    int src = x - dx;
    if (src < 0) {
        return 0;
    }
    if (src >= ROW_BYTES) {
        return ROW_BYTES - 1;
    }
    return src;
}

static int apply_row(uint8_t codec, int dx, const uint8_t *data, uint16_t comp, uint8_t *target, const uint8_t *previous)
{
    if (codec == 0) {
        if (dx != 0 || comp != ROW_BYTES) {
            return -1;
        }
        memcpy(target, data, ROW_BYTES);
        return 0;
    }
    if (comp == 0 || comp >= ROW_BYTES) {
        return -1;
    }
    if (codec == 1) {
        if (dx != 0) {
            return -1;
        }
        return rle_decode(data, comp, target, ROW_BYTES);
    }
    if (codec == 3) {
        if (dx != 0 || rle_decode(data, comp, target, ROW_BYTES) != 0) {
            return -1;
        }
        uint8_t acc = target[0];
        for (int x = 1; x < ROW_BYTES; x++) {
            acc = (uint8_t)(acc + target[x]);
            target[x] = acc;
        }
        return 0;
    }
    if (codec != 2 || previous == NULL || dx < -16 || dx > 16) {
        return -1;
    }
    uint8_t residual[ROW_BYTES];
    if (rle_decode(data, comp, residual, ROW_BYTES) != 0) {
        return -1;
    }
    for (int x = 0; x < ROW_BYTES; x++) {
        target[x] = (uint8_t)(previous[pred_x(x, dx)] + residual[x]);
    }
    return 0;
}

static int mask_pop(const uint8_t *mask)
{
    int count = 0;
    for (int i = 0; i < ROW_MASK_BYTES; i++) {
        uint8_t bits = mask[i];
        while (bits) {
            count += bits & 1;
            bits >>= 1;
        }
    }
    return count;
}

int w154_apply(const uint8_t *body, uint32_t len, uint8_t *dst, const uint8_t *src, uint16_t *palette, uint8_t *rowmask)
{
    if (len < PREAMBLE_BYTES + 4 || len > ASM_BYTES) {
        return -1;
    }
    uint32_t stored = rd_u32(body + len - 4);
    if (crc32_of(body, len - 4) != stored) {
        return -1;
    }
    uint16_t flags = rd_u16(body);
    uint16_t row_count = rd_u16(body + 2);
    const uint8_t *mask = body + 4;
    if ((flags & ~0x3u) != 0 || mask_pop(mask) != row_count) {
        return -1;
    }

    int key = (flags & 0x2) != 0;
    const uint8_t *p = body + PREAMBLE_BYTES;
    const uint8_t *end = body + len - 4;
    if (key) {
        int full = 1;
        for (int i = 0; i < ROW_MASK_BYTES; i++) {
            if (mask[i] != 0xFF) {
                full = 0;
            }
        }
        if (flags != 3 || row_count != ROW_COUNT || !full) {
            return -1;
        }
        if ((size_t)(end - p) < PALETTE_COLORS * 2) {
            return -1;
        }
        for (int i = 0; i < PALETTE_COLORS; i++) {
            palette[i] = rd_u16(p);
            p += 2;
        }
    } else if (flags != 0 || src == NULL) {
        return -1;
    } else {
        memcpy(dst, src, INDEX_BYTES);
    }

    memset(rowmask, 0, ROW_MASK_BYTES);
    int seen = 0;
    for (int row = 0; row < ROW_COUNT; row++) {
        if ((mask[row >> 3] & (uint8_t)(1u << (row & 7))) == 0) {
            continue;
        }
        if ((size_t)(end - p) < 4) {
            return -1;
        }
        uint8_t codec = p[0];
        int dx = (int8_t)p[1];
        uint16_t comp = rd_u16(p + 2);
        p += 4;
        if (codec > 3 || (key && codec == 2) || (!key && codec == 3)) {
            return -1;
        }
        if ((size_t)(end - p) < comp) {
            return -1;
        }
        const uint8_t *previous = src == NULL ? NULL : src + row * ROW_BYTES;
        if (apply_row(codec, dx, p, comp, dst + row * ROW_BYTES, previous) != 0) {
            return -1;
        }
        p += comp;
        int pad = (4 - (comp & 3)) & 3;
        for (int i = 0; i < pad; i++) {
            if (p >= end || *p++ != 0) {
                return -1;
            }
        }
        rowmask[row >> 3] = (uint8_t)(rowmask[row >> 3] | (uint8_t)(1u << (row & 7)));
        seen++;
    }
    if (seen != row_count || p != end) {
        return -1;
    }
    return key ? 1 : 0;
}

void decode_init(void)
{
    for (uint32_t i = 0; i < 256; i++) {
        uint32_t crc = i;
        for (int bit = 0; bit < 8; bit++) {
            crc = (crc & 1u) ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        s_crc_table[i] = crc;
    }
}

void decode_reset(void)
{
    s_have_frame = 0;
    s_next_frame = 0;
    s_epoch = atomic_load(&g.epoch);
}

void decode_task(void *arg)
{
    (void)arg;
    for (;;) {
        uint32_t epoch = atomic_load(&g.epoch);
        if (epoch != s_epoch) {
            decode_reset();
            s_have_frame = 0;
        }
        int slot = -1;
        for (int i = 0; i < 2; i++) {
            if (atomic_load(&g_slot[i].ready) == 0 && atomic_load(&g.reading) != i) {
                slot = i;
                break;
            }
        }
        if (slot < 0) {
            vTaskDelay(pdMS_TO_TICKS(1));
            continue;
        }
        uint8_t *body = NULL;
        uint32_t len = 0;
        uint32_t frame = 0;
        if (!ring_take(&body, &len, &frame)) {
            vTaskDelay(pdMS_TO_TICKS(1));
            continue;
        }
        if (atomic_load(&g.epoch) != epoch) {
            continue;
        }
        if (s_have_frame && frame != s_next_frame) {
            if (len < 2 || (body[0] & 0x02) == 0) {
                continue;
            }
        }
        frame_slot_t *dst = &g_slot[slot];
        const uint8_t *src = NULL;
        uint16_t palette[PALETTE_COLORS];
        memset(palette, 0, sizeof(palette));
        if (s_have_frame) {
            src = g_slot[1 - slot].index;
            memcpy(palette, g_slot[1 - slot].palette, sizeof(palette));
        }
        int key = w154_apply(body, len, dst->index, src, palette, dst->rowmask);
        if (key < 0) {
            ESP_LOGW(TAG, "body do frame %u inválido", (unsigned)frame);
            if (s_have_frame && frame == s_next_frame) {
                s_have_frame = 0;
            }
            continue;
        }
        if (atomic_load(&g.epoch) != epoch) {
            continue;
        }
        memcpy(dst->palette, palette, sizeof(palette));
        dst->frame = frame;
        atomic_store(&g_slot[slot].ready, 1);
        s_have_frame = 1;
        s_next_frame = frame + 1;
    }
}
