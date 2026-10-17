#pragma once

#include <stdint.h>

void decode_init(void);
void decode_reset(void);
void decode_task(void *arg);

/* [w154_apply] Decodifica um body para o quadro indexado. 0 se o CRC e as bands fecham. */
int w154_apply(const uint8_t *body, uint32_t len, uint8_t *dst, const uint8_t *src, uint16_t *palette, uint8_t *rowmask);
