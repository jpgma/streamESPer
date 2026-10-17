// SPDX-License-Identifier: LGPL-2.1-or-later

#include "decode.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int g_failed;

static void expect(int cond, const char *name)
{
    if (cond)
        return;
    fprintf(stderr, "falhou: %s\n", name);
    g_failed = 1;
}

static void put_u16(uint8_t *p, uint16_t value)
{
    p[0] = (uint8_t)value;
    p[1] = (uint8_t)(value >> 8);
}

static void put_u32(uint8_t *p, uint32_t value)
{
    p[0] = (uint8_t)value;
    p[1] = (uint8_t)(value >> 8);
    p[2] = (uint8_t)(value >> 16);
    p[3] = (uint8_t)(value >> 24);
}

static uint32_t read_u32_at(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint8_t *build_keyframe(size_t *out_len)
{
    size_t len = 8 + W154_PALETTE_COLORS * 2 + (size_t)W154_BAND_COUNT * (4 + W154_BAND_BYTES) + 4;
    uint8_t *body = calloc(1, len);
    if (body == NULL)
        return NULL;
    put_u16(body, 3);
    put_u16(body + 2, W154_BAND_COUNT);
    put_u32(body + 4, W154_KEYFRAME_MASK);
    for (int i = 0; i < W154_PALETTE_COLORS; i++)
        put_u16(body + 8 + i * 2, (uint16_t)i);
    size_t pos = 8 + W154_PALETTE_COLORS * 2;
    for (int band = 0; band < W154_BAND_COUNT; band++) {
        body[pos++] = 0;
        body[pos++] = 0;
        put_u16(body + pos, W154_BAND_BYTES);
        pos += 2;
        pos += W154_BAND_BYTES;
    }
    put_u32(body + pos, w154_crc32(body, pos));
    *out_len = len;
    return body;
}

static uint8_t *build_delta_band0(size_t *out_len)
{
    size_t len = 8 + 4 + W154_BAND_BYTES + 4;
    uint8_t *body = calloc(1, len);
    if (body == NULL)
        return NULL;
    put_u16(body, 0);
    put_u16(body + 2, 1);
    put_u32(body + 4, 1u);
    body[8] = 0;
    body[9] = 0;
    put_u16(body + 10, W154_BAND_BYTES);
    memset(body + 12, 7, W154_BAND_BYTES);
    size_t pos = 12 + W154_BAND_BYTES;
    put_u32(body + pos, w154_crc32(body, pos));
    *out_len = len;
    return body;
}

typedef struct byte_buf {
    uint8_t *data;
    size_t len;
    size_t cap;
} byte_buf;

static int buf_add(byte_buf *buf, const void *src, size_t n)
{
    if (buf->len + n > buf->cap) {
        size_t cap = buf->cap == 0 ? 256 : buf->cap;
        while (cap < buf->len + n)
            cap *= 2;
        uint8_t *grown = realloc(buf->data, cap);
        if (grown == NULL)
            return -1;
        buf->data = grown;
        buf->cap = cap;
    }
    memcpy(buf->data + buf->len, src, n);
    buf->len += n;
    return 0;
}

static int buf_u32(byte_buf *buf, uint32_t value)
{
    uint8_t bytes[4];
    put_u32(bytes, value);
    return buf_add(buf, bytes, 4);
}

static int wrap_file(byte_buf *file, const uint8_t *body0, size_t len0, const uint8_t *body1, size_t len1,
                     const uint8_t *pcm, uint32_t pcm_bytes, uint32_t sample_rate)
{
    uint32_t offset0 = W154_HEADER_SIZE + 8;
    uint32_t offset1 = offset0 + 4u + (uint32_t)len0;
    uint32_t audio_offset = pcm == NULL ? 0u : offset1 + 4u + (uint32_t)len1;

    uint8_t header[W154_HEADER_SIZE];
    memset(header, 0, sizeof header);
    header[0] = 'W';
    header[1] = '1';
    header[2] = '5';
    header[3] = '4';
    put_u16(header + 4, 1);
    put_u16(header + 6, W154_HEADER_SIZE);
    put_u16(header + 8, W154_WIDTH);
    put_u16(header + 10, W154_HEIGHT);
    put_u16(header + 12, 8);
    put_u16(header + 14, W154_BAND_COUNT);
    put_u32(header + 16, 41667);
    put_u32(header + 20, 2);
    put_u32(header + 28, audio_offset);

    if (buf_add(file, header, sizeof header) != 0 || buf_u32(file, offset0) != 0 || buf_u32(file, offset1) != 0)
        return -1;
    if (buf_u32(file, (uint32_t)len0) != 0 || buf_add(file, body0, len0) != 0)
        return -1;
    if (buf_u32(file, (uint32_t)len1) != 0 || buf_add(file, body1, len1) != 0)
        return -1;
    if (pcm == NULL)
        return 0;

    uint8_t preamble[16];
    put_u32(preamble, pcm_bytes);
    put_u32(preamble + 4, sample_rate);
    put_u16(preamble + 8, 1);
    put_u16(preamble + 10, 0);
    put_u32(preamble + 12, w154_crc32(pcm, pcm_bytes));
    return buf_add(file, preamble, sizeof preamble) != 0 || buf_add(file, pcm, pcm_bytes) != 0 ? -1 : 0;
}

static int decode_at(w154_picture *picture, const uint8_t *file, size_t len, uint32_t offset,
                     uint32_t index, uint32_t gop)
{
    if ((size_t)offset + 4 > len)
        return -1;
    uint32_t body_len = read_u32_at(file + offset);
    if (body_len > len || (size_t)offset + 4u + body_len > len)
        return -1;
    return w154_decode_body(file + offset + 4, body_len, index, gop, picture);
}

static int indexes_match(const uint8_t *file, size_t len, int with_audio)
{
    w154_file_info info;
    if (w154_parse_header(file, &info) != 0)
        return -1;
    if (info.frame_count != 2 || info.frame_us != 41667 || info.gop != 24)
        return -1;

    uint32_t offset0 = read_u32_at(file + W154_HEADER_SIZE);
    uint32_t offset1 = read_u32_at(file + W154_HEADER_SIZE + 4);
    if (offset0 != W154_HEADER_SIZE + 8)
        return -1;

    w154_picture picture;
    memset(&picture, 0, sizeof picture);
    if (decode_at(&picture, file, len, offset0, 0, info.gop) != 0)
        return -1;
    if (picture.index[0] != 0 || picture.palette[7] != 7)
        return -1;

    uint8_t *rgb = malloc((size_t)W154_PIXELS * 2u);
    if (rgb == NULL)
        return -1;
    w154_expand_rgb565(&picture, rgb);
    int red_ok = rgb[0] == 0 && rgb[1] == 0;
    free(rgb);
    if (!red_ok)
        return -1;

    if (decode_at(&picture, file, len, offset1, 1, info.gop) != 0)
        return -1;
    for (int i = 0; i < W154_BAND_BYTES; i++) {
        if (picture.index[i] != 7)
            return -1;
    }
    if (picture.index[W154_BAND_BYTES] != 0)
        return -1;

    rgb = malloc((size_t)W154_PIXELS * 2u);
    if (rgb == NULL)
        return -1;
    w154_expand_rgb565(&picture, rgb);
    int green_ok = rgb[0] == 7 && rgb[1] == 0;
    free(rgb);
    if (!green_ok)
        return -1;

    if (!with_audio)
        return info.audio_offset == 0 ? 0 : -1;

    if ((size_t)info.audio_offset + 16 > len)
        return -1;
    uint32_t body1 = read_u32_at(file + offset1);
    if (info.audio_offset != offset1 + 4u + body1)
        return -1;

    w154_audio_info audio;
    if (w154_parse_audio(file + info.audio_offset, info.frame_count, info.frame_us, &audio) != 0)
        return -1;
    if (audio.sample_rate != 48000 || audio.sample_count != 4000 || audio.pcm_bytes != 8000)
        return -1;
    if ((size_t)info.audio_offset + 16u + audio.pcm_bytes != len)
        return -1;
    const uint8_t *pcm = file + info.audio_offset + 16;
    if (w154_crc32(pcm, audio.pcm_bytes) != audio.crc32)
        return -1;
    return pcm[0] == 0x11 && pcm[1] == 0x00 ? 0 : -1;
}

static void check_audio_rejects(void)
{
    uint8_t preamble[16];
    memset(preamble, 0, sizeof preamble);
    w154_audio_info audio;

    put_u32(preamble, 2);
    put_u32(preamble + 4, 22050);
    put_u16(preamble + 8, 1);
    expect(w154_parse_audio(preamble, 2, 41667, &audio) != 0, "recusa 22050");

    memset(preamble, 0, sizeof preamble);
    put_u32(preamble, 8000);
    put_u32(preamble + 4, 48000);
    put_u16(preamble + 8, 2);
    expect(w154_parse_audio(preamble, 2, 41667, &audio) != 0, "recusa channels = 2");

    memset(preamble, 0, sizeof preamble);
    put_u32(preamble, 1);
    put_u32(preamble + 4, 48000);
    put_u16(preamble + 8, 1);
    expect(w154_parse_audio(preamble, 2, 41667, &audio) != 0, "recusa pcm_bytes impar");
}

static void check_vectors(void)
{
    const uint8_t digits[] = "123456789";
    expect(w154_crc32(digits, 9) == 0xCBF43926u, "crc32 de 123456789");

    uint8_t rle[30];
    for (int i = 0; i < 15; i++) {
        rle[i * 2] = 0xFF;
        rle[i * 2 + 1] = 0x07;
    }
    uint8_t band[W154_BAND_BYTES];
    expect(w154_rle_decode(rle, sizeof rle, band, sizeof band) == 0, "RLE de indice 7");
    int flat = 1;
    for (int i = 0; i < W154_BAND_BYTES; i++) {
        if (band[i] != 7)
            flat = 0;
    }
    expect(flat, "RLE preenche 1920 bytes");

    uint8_t lz4[11];
    lz4[0] = 0xA0;
    for (int i = 0; i < 10; i++)
        lz4[1 + i] = (uint8_t)(i + 1);
    uint8_t literals[10];
    expect(w154_lz4_decode(lz4, sizeof lz4, literals, sizeof literals) == 0, "LZ4 so de literais");
    int literals_ok = 1;
    for (int i = 0; i < 10; i++) {
        if (literals[i] != (uint8_t)(i + 1))
            literals_ok = 0;
    }
    expect(literals_ok, "LZ4 copia os literais");

    expect(w154_sample_count(2, 41667, 48000) == 4000, "dois quadros a 48000 Hz");
    expect(w154_sample_count(24, 41667, 48000) == 48000, "um segundo a 48000 Hz");
    expect(w154_sample_count(24, 41667, 16000) == 16000, "um segundo a 16000 Hz");
    expect(w154_sample_time(2000, 48000) == 41666, "amostra 2000");
    expect(w154_first_sample(41667, 48000, 4000) == 2001, "primeira amostra do quadro 1");
    expect(w154_first_sample(0, 48000, 4000) == 0, "primeira amostra do quadro 0");
}

int main(int argc, char **argv)
{
    check_vectors();
    check_audio_rejects();

    size_t key_len = 0;
    size_t delta_len = 0;
    uint8_t *key = build_keyframe(&key_len);
    uint8_t *delta = build_delta_band0(&delta_len);
    expect(key != NULL && delta != NULL && key_len == 58244, "keyframe cru de 58244");

    w154_picture bare;
    memset(&bare, 0, sizeof bare);
    if (delta != NULL)
        expect(w154_decode_body(delta, delta_len, 1, 24, &bare) != 0, "delta sem quadro anterior");

    byte_buf video = {0};
    byte_buf with_audio = {0};
    uint8_t pcm[8000];
    for (int i = 0; i < 4000; i++) {
        pcm[i * 2] = 0x11;
        pcm[i * 2 + 1] = 0x00;
    }

    if (key != NULL && delta != NULL) {
        expect(wrap_file(&video, key, key_len, delta, delta_len, NULL, 0, 0) == 0, "arquivo so de video");
        expect(indexes_match(video.data, video.len, 0) == 0, "decode do arquivo so de video");
        expect(wrap_file(&with_audio, key, key_len, delta, delta_len, pcm, sizeof pcm, 48000) == 0,
               "arquivo com PCM");
        expect(indexes_match(with_audio.data, with_audio.len, 1) == 0, "decode do arquivo com PCM");
    }

    if (argc >= 2 && with_audio.data != NULL) {
        FILE *out = fopen(argv[1], "wb");
        if (out == NULL) {
            fprintf(stderr, "falhou: gravar %s\n", argv[1]);
            g_failed = 1;
        } else {
            if (fwrite(with_audio.data, 1, with_audio.len, out) != with_audio.len)
                g_failed = 1;
            fclose(out);
        }
    }

    free(key);
    free(delta);
    free(video.data);
    free(with_audio.data);

    if (g_failed)
        return 1;
    printf("selftest ok\n");
    return 0;
}
