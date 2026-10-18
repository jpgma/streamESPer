#pragma once

#include <stddef.h>
#include <stdint.h>

void ring_mount(uint8_t *base, uint32_t cap, uint8_t *scratch);
void ring_clear(void);
uint32_t ring_capacity(void);
uint32_t ring_free_bytes(void);

/* [ring_queued] Bodies completos ainda não entregues ao decode. */
uint32_t ring_queued(void);

/* [ring_video] Guarda um fragmento em ordem. 0 aceito, 1 ignorado, -1 sem espaço. */
int ring_video(uint32_t seq, uint32_t frame, uint16_t frag, uint16_t frag_count, const uint8_t *payload, uint16_t len);

/* [ring_take] Copia o body mais antigo para scratch. 1 se havia body. */
int ring_take(uint8_t **scratch, uint32_t *len, uint32_t *frame);

/* [ring_diag] Bodies na fila e quantos datagramas foram pulados desde a última leitura. */
void ring_diag(uint32_t *queued, uint32_t *skipped);
