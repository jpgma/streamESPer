// SPDX-License-Identifier: LGPL-2.1-or-later

#ifndef W154_DECODE_H
#define W154_DECODE_H

#include <stddef.h>
#include <stdint.h>

#define W154_WIDTH 240
#define W154_HEIGHT 240
#define W154_PIXELS (W154_WIDTH * W154_HEIGHT)
#define W154_BAND_COUNT 30
#define W154_BAND_BYTES (W154_WIDTH * 8)
#define W154_PALETTE_COLORS 256
#define W154_HEADER_SIZE 32
#define W154_MIN_BODY 12
#define W154_MAX_BODY 58244
#define W154_KEYFRAME_MASK 0x3FFFFFFFu

typedef struct w154_file_info {
    uint32_t frame_us;
    uint32_t frame_count;
    uint32_t audio_offset;
    uint32_t gop;
} w154_file_info;

typedef struct w154_audio_info {
    uint32_t pcm_bytes;
    uint32_t sample_rate;
    uint32_t sample_count;
    uint32_t crc32;
} w154_audio_info;

typedef struct w154_picture {
    uint8_t index[W154_PIXELS];
    uint16_t palette[W154_PALETTE_COLORS];
    int ready;
} w154_picture;

uint32_t w154_crc32(const uint8_t *data, size_t len);
uint32_t w154_crc32_update(uint32_t crc, const uint8_t *data, size_t len);
uint32_t w154_crc32_finish(uint32_t crc);

int w154_rle_decode(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len);
int w154_lz4_decode(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_len);

int w154_parse_header(const uint8_t header[W154_HEADER_SIZE], w154_file_info *out);

uint32_t w154_sample_count(uint32_t frame_count, uint32_t frame_us, uint32_t sample_rate);
uint64_t w154_sample_time(uint32_t index, uint32_t sample_rate);
uint32_t w154_first_sample(uint64_t time_us, uint32_t sample_rate, uint32_t sample_count);

int w154_parse_audio(const uint8_t preamble[16], uint32_t frame_count, uint32_t frame_us,
                     w154_audio_info *out);

int w154_decode_body(const uint8_t *body, size_t body_len, uint32_t index, uint32_t gop,
                     w154_picture *picture);

void w154_expand_rgb565(const w154_picture *picture, uint8_t *dst);

#endif
