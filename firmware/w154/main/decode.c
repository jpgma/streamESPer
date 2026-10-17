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

static int rle_decode(const uint8_t *src, uint32_t src_len, uint8_t *dst)
{
    uint32_t ip = 0;
    uint32_t op = 0;
    while (op < BAND_BYTES) {
        if (ip >= src_len) {
            return -1;
        }
        uint8_t ctl = src[ip++];
        if (ctl < 128) {
            uint32_t n = (uint32_t)ctl + 1u;
            if (ip + n > src_len || op + n > BAND_BYTES) {
                return -1;
            }
            memcpy(dst + op, src + ip, n);
            ip += n;
            op += n;
        } else {
            uint32_t n = (uint32_t)ctl - 127u;
            if (ip >= src_len || op + n > BAND_BYTES) {
                return -1;
            }
            memset(dst + op, src[ip++], n);
            op += n;
        }
    }
    return op == BAND_BYTES && ip == src_len ? 0 : -1;
}

static int read_len(const uint8_t *src, uint32_t src_len, uint32_t *ip, int nibble, int *out)
{
    int length = nibble;
    if (nibble != 15) {
        *out = length;
        return 0;
    }
    for (;;) {
        if (*ip >= src_len) {
            return -1;
        }
        uint8_t extra = src[(*ip)++];
        length += extra;
        if (extra != 255) {
            *out = length;
            return 0;
        }
    }
}

static int lz4_decode(const uint8_t *src, uint32_t src_len, uint8_t *dst)
{
    uint32_t ip = 0;
    uint32_t op = 0;
    while (ip < src_len) {
        uint8_t token = src[ip++];
        int lit_len = 0;
        if (read_len(src, src_len, &ip, token >> 4, &lit_len) != 0 || lit_len < 0) {
            return -1;
        }
        if ((uint32_t)lit_len > BAND_BYTES - op || ip + (uint32_t)lit_len > src_len) {
            return -1;
        }
        memcpy(dst + op, src + ip, (size_t)lit_len);
        ip += (uint32_t)lit_len;
        op += (uint32_t)lit_len;
        if (ip >= src_len) {
            break;
        }
        if (ip + 2 > src_len) {
            return -1;
        }
        uint32_t offset = (uint32_t)src[ip] | ((uint32_t)src[ip + 1] << 8);
        ip += 2;
        if (offset == 0 || offset > op) {
            return -1;
        }
        int extra = 0;
        if (read_len(src, src_len, &ip, token & 0x0F, &extra) != 0) {
            return -1;
        }
        uint32_t match_len = (uint32_t)extra + 4u;
        if (op + match_len > BAND_BYTES) {
            return -1;
        }
        uint32_t match = op - offset;
        for (uint32_t i = 0; i < match_len; i++) {
            dst[op++] = dst[match++];
        }
    }
    return op == BAND_BYTES ? 0 : -1;
}

static void mark_band(uint8_t *rowmask, int band)
{
    for (int row = band * BAND_ROWS; row < (band + 1) * BAND_ROWS; row++) {
        rowmask[row >> 3] = (uint8_t)(rowmask[row >> 3] | (uint8_t)(1u << (row & 7)));
    }
}

int w154_apply(const uint8_t *body, uint32_t len, uint8_t *dst, const uint8_t *src, uint16_t *palette, uint8_t *rowmask)
{
    if (len < 12 || len > ASM_BYTES) {
        return -1;
    }
    uint32_t stored = rd_u32(body + len - 4);
    if (crc32_of(body, len - 4) != stored) {
        return -1;
    }
    uint16_t flags = rd_u16(body);
    uint16_t band_count = rd_u16(body + 2);
    uint32_t mask = rd_u32(body + 4);
    if ((flags & ~0x3u) != 0 || (mask & 0xC0000000u) != 0) {
        return -1;
    }
    uint32_t bits = mask;
    int pop = 0;
    while (bits) {
        pop += (int)(bits & 1u);
        bits >>= 1;
    }
    if (pop != band_count) {
        return -1;
    }

    int key = (flags & 0x2) != 0;
    const uint8_t *p = body + 8;
    const uint8_t *end = body + len - 4;
    if (key) {
        if (flags != 3 || band_count != BAND_COUNT || mask != 0x3FFFFFFFu) {
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

    memset(rowmask, 0, 30);
    int seen = 0;
    for (int band = 0; band < BAND_COUNT; band++) {
        if ((mask & (1u << band)) == 0) {
            continue;
        }
        if ((size_t)(end - p) < 4) {
            return -1;
        }
        uint8_t codec = p[0];
        if (p[1] != 0) {
            return -1;
        }
        uint16_t comp = rd_u16(p + 2);
        p += 4;
        if (codec == 0 && comp != BAND_BYTES) {
            return -1;
        }
        if ((codec == 1 || codec == 2) && (comp == 0 || comp >= BAND_BYTES)) {
            return -1;
        }
        if (codec > 2 || (size_t)(end - p) < comp) {
            return -1;
        }
        uint8_t *target = dst + band * BAND_BYTES;
        int ok = -1;
        if (codec == 0) {
            memcpy(target, p, BAND_BYTES);
            ok = 0;
        } else if (codec == 1) {
            ok = rle_decode(p, comp, target);
        } else {
            ok = lz4_decode(p, comp, target);
        }
        if (ok != 0) {
            return -1;
        }
        p += comp;
        int pad = (4 - (comp & 3)) & 3;
        for (int i = 0; i < pad; i++) {
            if (p >= end || *p++ != 0) {
                return -1;
            }
        }
        mark_band(rowmask, band);
        seen++;
    }
    if (seen != band_count || p != end) {
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
