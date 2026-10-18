#pragma once

#include <stdint.h>

/* Datagramas de sessão. Vídeo e áudio seguem docs/formato-w154.md. */

#define SESSION_BYTES 16
#define VIDEO_PAYLOAD 1456
#define UDP_PACKET 1472

#define HELLO_MAGIC 0x34353148u
#define CREDIT_MAGIC 0x34353143u
#define CONTROL_MAGIC 0x3435314Bu

#define OP_RESET 1u
#define OP_PAUSE 2u
#define OP_RESUME 3u
#define OP_AUDIO 4u
#define OP_EOS 5u

#define CREDIT_FLAG_UNDERRUN (1u << 30)
#define CREDIT_FLAG_PAUSED (1u << 31)
#define CREDIT_FRAME_MASK 0x3FFFFFFFu

#define VIDEO_PORT 5001
#define AUDIO_PORT 5002
#define CONTROL_PORT 5003

#define INDEX_BYTES 57600
#define ASM_BYTES 58244
#define BAND_ROWS 8
#define BAND_COUNT 30
#define BAND_BYTES (240 * BAND_ROWS)
#define PALETTE_COLORS 256
#define RING_SLOTS 512

static inline uint16_t rd_u16(const uint8_t *p)
{
    return (uint16_t)p[0] | ((uint16_t)p[1] << 8);
}

static inline uint32_t rd_u32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static inline void wr_u16(uint8_t *p, uint16_t v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
}

static inline void wr_u32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
    p[2] = (uint8_t)(v >> 16);
    p[3] = (uint8_t)(v >> 24);
}
