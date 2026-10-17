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

static int read_length(const uint8_t *src, size_t src_len, size_t *ip, unsigned nibble, size_t *length)
{
    size_t len = nibble;
    if (nibble == 15) {
        for (;;) {
            if (*ip >= src_len)
                return -1;
            unsigned extra = src[(*ip)++];
            if (len + extra < len)
                return -1;
            len += extra;
            if (extra != 255)
                break;
        }
    }
    *length = len;
    return 0;
}

int w154_rle_decode(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len)
{
    if (dst_len != W154_BAND_BYTES)
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

int w154_lz4_decode(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len)
{
    size_t ip = 0;
    size_t op = 0;
    while (ip < src_len) {
        uint8_t token = src[ip++];
        size_t lit_len = 0;
        if (read_length(src, src_len, &ip, token >> 4, &lit_len) != 0)
            return -1;
        if (op + lit_len > dst_len || ip + lit_len > src_len)
            return -1;
        memcpy(dst + op, src + ip, lit_len);
        ip += lit_len;
        op += lit_len;
        if (ip >= src_len)
            break;
        if (ip + 2 > src_len)
            return -1;

        unsigned offset = (unsigned)src[ip] | ((unsigned)src[ip + 1] << 8);
        ip += 2;
        if (offset == 0 || offset > op)
            return -1;

        size_t match_extra = 0;
        if (read_length(src, src_len, &ip, token & 0x0Fu, &match_extra) != 0)
            return -1;
        size_t match_len = match_extra + 4;
        if (op + match_len > dst_len)
            return -1;

        size_t match = op - offset;
        for (size_t i = 0; i < match_len; i++)
            dst[op++] = dst[match++];
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
    if (read_u16(header + 4) != 1 || read_u16(header + 6) != W154_HEADER_SIZE)
        return -1;
    if (read_u16(header + 8) != W154_WIDTH || read_u16(header + 10) != W154_HEIGHT)
        return -1;
    if (read_u16(header + 12) != 8 || read_u16(header + 14) != W154_BAND_COUNT)
        return -1;

    uint32_t frame_us = read_u32(header + 16);
    uint32_t frame_count = read_u32(header + 20);
    if (read_u32(header + 24) != 0)
        return -1;
    if (frame_count < 1 || frame_us < 16667 || frame_us > 1000000)
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

    if (pcm_bytes == 0 || (pcm_bytes & 1u) != 0)
        return -1;
    if (!rate_ok(sample_rate) || channels != 1 || sample_format != 0)
        return -1;

    uint32_t sample_count = w154_sample_count(frame_count, frame_us, sample_rate);
    if (sample_count == 0 || pcm_bytes != sample_count * 2u)
        return -1;

    out->pcm_bytes = pcm_bytes;
    out->sample_rate = sample_rate;
    out->sample_count = sample_count;
    out->crc32 = crc;
    return 0;
}

static int popcount(uint32_t value)
{
    int count = 0;
    while (value != 0) {
        count += (int)(value & 1u);
        value >>= 1;
    }
    return count;
}

int w154_decode_body(const uint8_t *body, size_t body_len, uint32_t index, uint32_t gop,
                     w154_picture *picture)
{
    if (body_len < W154_MIN_BODY || body_len > W154_MAX_BODY || gop == 0)
        return -1;

    uint16_t flags = read_u16(body);
    uint16_t band_count = read_u16(body + 2);
    uint32_t mask = read_u32(body + 4);
    if ((flags & ~0x3u) != 0 || (mask & 0xC0000000u) != 0)
        return -1;
    if (popcount(mask) != band_count || band_count > W154_BAND_COUNT)
        return -1;

    int keyframe = index % gop == 0;
    size_t pos = 8;
    uint16_t palette[W154_PALETTE_COLORS];
    if (keyframe) {
        if (flags != 3 || band_count != W154_BAND_COUNT || mask != W154_KEYFRAME_MASK)
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
    for (int band = 0; band < W154_BAND_COUNT; band++) {
        if ((mask & (1u << band)) == 0)
            continue;
        if (pos + 4 > body_len - 4) {
            free(next);
            return -1;
        }
        uint8_t codec = body[pos++];
        if (body[pos++] != 0) {
            free(next);
            return -1;
        }
        uint16_t comp = read_u16(body + pos);
        pos += 2;
        if (codec == 0 && comp != W154_BAND_BYTES) {
            free(next);
            return -1;
        }
        if ((codec == 1 || codec == 2) && (comp == 0 || comp >= W154_BAND_BYTES)) {
            free(next);
            return -1;
        }
        if (codec > 2) {
            free(next);
            return -1;
        }
        if (pos + comp > body_len - 4) {
            free(next);
            return -1;
        }

        uint8_t *target = next + (size_t)band * W154_BAND_BYTES;
        int ok;
        if (codec == 0) {
            memcpy(target, body + pos, W154_BAND_BYTES);
            ok = 1;
        } else if (codec == 1) {
            ok = w154_rle_decode(body + pos, comp, target, W154_BAND_BYTES) == 0;
        } else {
            ok = w154_lz4_decode(body + pos, comp, target, W154_BAND_BYTES) == 0;
        }
        pos += comp;
        if (!ok) {
            free(next);
            return -1;
        }

        int pad = (4 - (comp & 3)) & 3;
        for (int p = 0; p < pad; p++) {
            if (pos >= body_len - 4 || body[pos++] != 0) {
                free(next);
                return -1;
            }
        }
        seen++;
    }

    if (seen != band_count || pos != body_len - 4) {
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
