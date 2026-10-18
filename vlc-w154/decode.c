// SPDX-License-Identifier: LGPL-2.1-or-later

#include "decode.h"

#include <stdlib.h>
#include <string.h>

static uint32_t crc_table[256];
static int crc_ready;

static void crc_init(void)
{
    if (crc_ready)
        return;
    for (uint32_t i = 0; i < 256; i++) {
        uint32_t crc = i;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 1u) ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        crc_table[i] = crc;
    }
    crc_ready = 1;
}

uint32_t w154_crc32_update(uint32_t crc, const uint8_t *data, size_t len)
{
    crc_init();
    for (size_t i = 0; i < len; i++)
        crc = crc_table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
    return crc;
}

uint32_t w154_crc32_finish(uint32_t crc)
{
    return crc ^ 0xFFFFFFFFu;
}

uint32_t w154_crc32(const uint8_t *data, size_t len)
{
    return w154_crc32_finish(w154_crc32_update(0xFFFFFFFFu, data, len));
}

static uint16_t read_u16(const uint8_t *p)
{
    return (uint16_t)p[0] | ((uint16_t)p[1] << 8);
}

static uint32_t read_u32(const uint8_t *p)
{
    return (uint32_t)p[0]
        | ((uint32_t)p[1] << 8)
        | ((uint32_t)p[2] << 16)
        | ((uint32_t)p[3] << 24);
}

int w154_rle_decode(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len)
{
    if (dst_len == 0)
        return -1;

    size_t ip = 0;
    size_t op = 0;
    while (op < dst_len) {
        if (ip >= src_len)
            return -1;
        uint8_t ctl = src[ip++];
        if (ctl < 128) {
            size_t n = (size_t)ctl + 1;
            if (ip + n > src_len || op + n > dst_len)
                return -1;
            memcpy(dst + op, src + ip, n);
            ip += n;
            op += n;
        } else {
            size_t n = (size_t)ctl - 127;
            if (ip >= src_len || op + n > dst_len)
                return -1;
            memset(dst + op, src[ip++], n);
            op += n;
        }
    }
    return op == dst_len && ip == src_len ? 0 : -1;
}

static const int ima_index[16] = {
    -1, -1, -1, -1, 2, 4, 6, 8,
    -1, -1, -1, -1, 2, 4, 6, 8,
};

static const int ima_step[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
    19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
    50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
    130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
    337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
    876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
    2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
    5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
};

static int ima_clamp(int value)
{
    if (value > 32767)
        return 32767;
    if (value < -32768)
        return -32768;
    return value;
}

static int ima_decode_nibble(int predictor, int *step_index, int nibble)
{
    int step = ima_step[*step_index];
    int diff = step >> 3;
    if (nibble & 4)
        diff += step;
    if (nibble & 2)
        diff += step >> 1;
    if (nibble & 1)
        diff += step >> 2;
    if (nibble & 8)
        predictor -= diff;
    else
        predictor += diff;
    predictor = ima_clamp(predictor);
    *step_index += ima_index[nibble & 15];
    if (*step_index < 0)
        *step_index = 0;
    else if (*step_index > 88)
        *step_index = 88;
    return predictor;
}

static uint8_t ima_encode_nibble(int *predictor, int *step_index, int sample)
{
    int step = ima_step[*step_index];
    int diff = sample - *predictor;
    int nibble = 0;
    if (diff < 0) {
        nibble = 8;
        diff = -diff;
    }
    int vpdiff = step >> 3;
    if (diff >= step) {
        nibble |= 4;
        diff -= step;
        vpdiff += step;
    }
    step >>= 1;
    if (diff >= step) {
        nibble |= 2;
        diff -= step;
        vpdiff += step;
    }
    step >>= 1;
    if (diff >= step) {
        nibble |= 1;
        vpdiff += step;
    }
    if (nibble & 8)
        *predictor -= vpdiff;
    else
        *predictor += vpdiff;
    *predictor = ima_clamp(*predictor);
    *step_index += ima_index[nibble];
    if (*step_index < 0)
        *step_index = 0;
    else if (*step_index > 88)
        *step_index = 88;
    return (uint8_t)nibble;
}

uint32_t w154_ima_block_bytes(uint32_t samples)
{
    uint32_t raw = 4u + samples / 2u;
    return (raw + 3u) & ~3u;
}

uint32_t w154_ima_payload_bytes(uint32_t sample_count)
{
    if (sample_count == 0)
        return 0;
    uint32_t full = sample_count / W154_IMA_SAMPLES;
    uint32_t rem = sample_count % W154_IMA_SAMPLES;
    return full * W154_IMA_BLOCK + (rem == 0 ? 0 : w154_ima_block_bytes(rem));
}

int w154_ima_decode_block(const uint8_t *block, uint32_t samples, int16_t *dst)
{
    if (samples == 0 || samples > W154_IMA_SAMPLES || block[2] > 88 || block[3] != 0)
        return -1;
    int predictor = (int16_t)((uint16_t)block[0] | ((uint16_t)block[1] << 8));
    int step_index = block[2];
    dst[0] = (int16_t)predictor;
    size_t ip = 4;
    int high = 0;
    for (uint32_t i = 1; i < samples; i++) {
        int nibble = high ? block[ip++] >> 4 : block[ip] & 0x0F;
        high = !high;
        predictor = ima_decode_nibble(predictor, &step_index, nibble);
        dst[i] = (int16_t)predictor;
    }
    return 0;
}

int w154_ima_encode(const int16_t *samples, uint32_t sample_count, uint8_t *dst, uint32_t dst_len)
{
    if (w154_ima_payload_bytes(sample_count) != dst_len)
        return -1;
    int step_index = 0;
    uint32_t at = 0;
    uint32_t op = 0;
    while (at < sample_count) {
        uint32_t count = sample_count - at;
        if (count > W154_IMA_SAMPLES)
            count = W154_IMA_SAMPLES;
        int predictor = samples[at];
        dst[op] = (uint8_t)predictor;
        dst[op + 1] = (uint8_t)(predictor >> 8);
        dst[op + 2] = (uint8_t)step_index;
        dst[op + 3] = 0;
        uint32_t nibble_at = op + 4;
        int high = 0;
        for (uint32_t i = 1; i < count; i++) {
            uint8_t nibble = ima_encode_nibble(&predictor, &step_index, samples[at + i]);
            if (!high) {
                dst[nibble_at] = nibble;
                high = 1;
            } else {
                dst[nibble_at] |= (uint8_t)(nibble << 4);
                nibble_at++;
                high = 0;
            }
        }
        op += w154_ima_block_bytes(count);
        at += count;
    }
    return op == dst_len ? 0 : -1;
}

static int rate_ok(uint32_t rate)
{
    return rate == 16000 || rate == 24000 || rate == 48000;
}

int w154_parse_header(const uint8_t header[W154_HEADER_SIZE], w154_file_info *out)
{
    if (header[0] != 'W' || header[1] != '1' || header[2] != '5' || header[3] != '4')
        return -1;
    if (read_u16(header + 4) != 2 || read_u16(header + 6) != W154_HEADER_SIZE)
        return -1;
    if (read_u16(header + 8) != W154_WIDTH || read_u16(header + 10) != W154_HEIGHT)
        return -1;
    if (read_u16(header + 12) != 1 || read_u16(header + 14) != W154_ROW_COUNT)
        return -1;

    uint32_t frame_us = read_u32(header + 16);
    uint32_t frame_count = read_u32(header + 20);
    if (read_u32(header + 24) != 0)
        return -1;
    if (frame_count < 1 || frame_us < 12500 || frame_us > 1000000)
        return -1;

    uint32_t gop = (1000000u + frame_us / 2u) / frame_us;
    if (gop < 1)
        gop = 1;

    out->frame_us = frame_us;
    out->frame_count = frame_count;
    out->audio_offset = read_u32(header + 28);
    out->gop = gop;
    return 0;
}

uint32_t w154_sample_count(uint32_t frame_count, uint32_t frame_us, uint32_t sample_rate)
{
    if (sample_rate == 0 || frame_us == 0)
        return 0;
    if (frame_count > (UINT64_MAX / frame_us))
        return 0;
    uint64_t timeline = (uint64_t)frame_count * frame_us;
    if (timeline > (UINT64_MAX - 500000ull) / sample_rate)
        return 0;
    uint64_t count = (timeline * sample_rate + 500000ull) / 1000000ull;
    if (count == 0 || count > UINT32_MAX / 2)
        return 0;
    return (uint32_t)count;
}

uint64_t w154_sample_time(uint32_t index, uint32_t sample_rate)
{
    if (sample_rate == 0)
        return 0;
    return ((uint64_t)index * 1000000ull) / sample_rate;
}

uint32_t w154_first_sample(uint64_t time_us, uint32_t sample_rate, uint32_t sample_count)
{
    if (sample_rate == 0 || sample_count == 0)
        return 0;
    if (time_us > UINT64_MAX / sample_rate)
        return sample_count;

    uint64_t index = (time_us * sample_rate) / 1000000ull;
    if (index > sample_count)
        index = sample_count;
    while (index < sample_count && w154_sample_time((uint32_t)index, sample_rate) < time_us)
        index++;
    return (uint32_t)index;
}

int w154_parse_audio(const uint8_t preamble[16], uint32_t frame_count, uint32_t frame_us,
                     w154_audio_info *out)
{
    uint32_t pcm_bytes = read_u32(preamble);
    uint32_t sample_rate = read_u32(preamble + 4);
    uint16_t channels = read_u16(preamble + 8);
    uint16_t sample_format = read_u16(preamble + 10);
    uint32_t crc = read_u32(preamble + 12);

    if (pcm_bytes == 0 || (pcm_bytes & 3u) != 0)
        return -1;
    if (!rate_ok(sample_rate) || channels != 1 || sample_format != 1)
        return -1;

    uint32_t sample_count = w154_sample_count(frame_count, frame_us, sample_rate);
    if (sample_count == 0 || pcm_bytes != w154_ima_payload_bytes(sample_count))
        return -1;

    out->pcm_bytes = pcm_bytes;
    out->sample_rate = sample_rate;
    out->sample_count = sample_count;
    out->crc32 = crc;
    return 0;
}

static int mask_pop(const uint8_t *mask)
{
    int count = 0;
    for (int i = 0; i < W154_ROW_MASK; i++) {
        unsigned bits = mask[i];
        while (bits != 0) {
            count += (int)(bits & 1u);
            bits >>= 1;
        }
    }
    return count;
}

static int pred_x(int x, int dx)
{
    int src = x - dx;
    if (src < 0)
        return 0;
    if (src >= W154_ROW_BYTES)
        return W154_ROW_BYTES - 1;
    return src;
}

static int apply_row(uint8_t codec, int dx, const uint8_t *data, uint16_t comp, uint8_t *target, const uint8_t *previous)
{
    if (codec == 0) {
        if (dx != 0 || comp != W154_ROW_BYTES)
            return -1;
        memcpy(target, data, W154_ROW_BYTES);
        return 0;
    }
    if (comp == 0 || comp >= W154_ROW_BYTES)
        return -1;
    if (codec == 1) {
        if (dx != 0)
            return -1;
        return w154_rle_decode(data, comp, target, W154_ROW_BYTES);
    }
    if (codec == 3) {
        if (dx != 0 || w154_rle_decode(data, comp, target, W154_ROW_BYTES) != 0)
            return -1;
        uint8_t acc = target[0];
        for (int x = 1; x < W154_ROW_BYTES; x++) {
            acc = (uint8_t)(acc + target[x]);
            target[x] = acc;
        }
        return 0;
    }
    if (codec != 2 || previous == NULL || dx < -16 || dx > 16)
        return -1;
    uint8_t residual[W154_ROW_BYTES];
    if (w154_rle_decode(data, comp, residual, W154_ROW_BYTES) != 0)
        return -1;
    for (int x = 0; x < W154_ROW_BYTES; x++)
        target[x] = (uint8_t)(previous[pred_x(x, dx)] + residual[x]);
    return 0;
}

int w154_decode_body(const uint8_t *body, size_t body_len, uint32_t index, uint32_t gop,
                     w154_picture *picture)
{
    if (body_len < W154_MIN_BODY || body_len > W154_MAX_BODY || gop == 0)
        return -1;

    uint16_t flags = read_u16(body);
    uint16_t row_count = read_u16(body + 2);
    const uint8_t *mask = body + 4;
    if ((flags & ~0x3u) != 0 || mask_pop(mask) != row_count)
        return -1;

    int keyframe = index % gop == 0;
    size_t pos = W154_PREAMBLE;
    uint16_t palette[W154_PALETTE_COLORS];
    if (keyframe) {
        int full = 1;
        for (int i = 0; i < W154_ROW_MASK; i++) {
            if (mask[i] != 0xFF)
                full = 0;
        }
        if (flags != 3 || row_count != W154_ROW_COUNT || !full)
            return -1;
        if (pos + W154_PALETTE_COLORS * 2u > body_len - 4)
            return -1;
        for (int i = 0; i < W154_PALETTE_COLORS; i++)
            palette[i] = read_u16(body + pos + (size_t)i * 2u);
        pos += W154_PALETTE_COLORS * 2u;
    } else if (flags != 0 || !picture->ready) {
        return -1;
    }

    uint8_t *next = malloc(W154_PIXELS);
    if (next == NULL)
        return -1;
    if (keyframe)
        memset(next, 0, W154_PIXELS);
    else
        memcpy(next, picture->index, W154_PIXELS);

    int seen = 0;
    for (int row = 0; row < W154_ROW_COUNT; row++) {
        if ((mask[row >> 3] & (uint8_t)(1u << (row & 7))) == 0)
            continue;
        if (pos + 4 > body_len - 4) {
            free(next);
            return -1;
        }
        uint8_t codec = body[pos++];
        int dx = (int8_t)body[pos++];
        uint16_t comp = read_u16(body + pos);
        pos += 2;
        if (codec > 3 || (keyframe && codec == 2) || (!keyframe && codec == 3) || pos + comp > body_len - 4) {
            free(next);
            return -1;
        }
        const uint8_t *previous = keyframe ? NULL : picture->index + (size_t)row * W154_ROW_BYTES;
        if (apply_row(codec, dx, body + pos, comp, next + (size_t)row * W154_ROW_BYTES, previous) != 0) {
            free(next);
            return -1;
        }
        pos += comp;
        int pad = (4 - (comp & 3)) & 3;
        for (int p = 0; p < pad; p++) {
            if (pos >= body_len - 4 || body[pos++] != 0) {
                free(next);
                return -1;
            }
        }
        seen++;
    }

    if (seen != row_count || pos != body_len - 4) {
        free(next);
        return -1;
    }

    uint32_t stored = read_u32(body + pos);
    uint32_t actual = w154_crc32(body, body_len - 4);
    if (stored != actual) {
        free(next);
        return -1;
    }

    memcpy(picture->index, next, W154_PIXELS);
    if (keyframe)
        memcpy(picture->palette, palette, sizeof palette);
    picture->ready = 1;
    free(next);
    return 0;
}

void w154_expand_rgb565(const w154_picture *picture, uint8_t *dst)
{
    for (int i = 0; i < W154_PIXELS; i++) {
        uint16_t color = picture->palette[picture->index[i]];
        dst[i * 2] = (uint8_t)color;
        dst[i * 2 + 1] = (uint8_t)(color >> 8);
    }
}
