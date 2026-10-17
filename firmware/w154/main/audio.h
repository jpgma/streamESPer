#pragma once

#include "driver/i2c_master.h"

void audio_init(i2c_master_bus_handle_t bus);
void audio_reset(void);
void audio_set_rate(uint32_t sample_rate);
void audio_push(const uint8_t *pcm, uint16_t bytes);
void audio_task(void *arg);
