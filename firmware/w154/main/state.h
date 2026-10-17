#pragma once

#include <stdatomic.h>
#include <stdint.h>

#include "proto.h"

/* bit0 pause do operador, bit1 underrun, bit2 eos */

#define PF_PAUSE 1u
#define PF_UNDERRUN 2u
#define PF_EOS 4u

typedef struct {
    atomic_uint token;
    atomic_uint epoch;
    atomic_uint frame_us;
    atomic_uint sample_rate;
    atomic_uint play_frame;
    atomic_uint expect_seq;
    atomic_uint flags;
    atomic_int have_ip;
    atomic_int reading;
    atomic_llong media_us;
    atomic_int clock_run;
} shared_t;

typedef struct {
    uint8_t *index;
    uint16_t palette[PALETTE_COLORS];
    uint8_t rowmask[30];
    uint32_t frame;
    atomic_int ready;
} frame_slot_t;

extern shared_t g;
extern frame_slot_t g_slot[2];

void player_on_reset(uint32_t token, uint32_t frame_us);
void player_on_pause(void);
void player_on_resume(void);
void player_on_audio(uint32_t sample_rate);
void player_on_eos(void);
