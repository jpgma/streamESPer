#include "ring.h"

#include <string.h>

#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "proto.h"
#include "state.h"

typedef struct {
    uint32_t offset;
    uint32_t length;
    uint32_t frame;
    uint16_t frags;
    uint16_t got;
    uint8_t complete;
} slot_t;

static uint8_t *s_base;
static uint8_t *s_scratch;
static uint32_t s_cap;
static uint32_t s_head;
static uint32_t s_tail;
static uint32_t s_used;
static int s_wrapped;
static slot_t s_q[RING_SLOTS];
static int s_qh;
static int s_qt;
static int s_qn;
static int s_partial;
static int s_skip_key;
static uint32_t s_expect;
static SemaphoreHandle_t s_mu;

static uint32_t tail_room(void)
{
    if (s_qn == 0 && !s_partial) {
        return s_cap;
    }
    if (s_wrapped) {
        return s_head > s_tail ? s_head - s_tail : 0;
    }
    return s_cap - s_tail;
}

static void rewind_partial(void)
{
    if (!s_partial) {
        return;
    }
    slot_t *slot = &s_q[s_qt];
    s_used -= slot->length;
    s_tail = slot->offset;
    if (s_qn == 0) {
        s_head = 0;
        s_tail = 0;
        s_used = 0;
        s_wrapped = 0;
    }
    s_partial = 0;
    memset(slot, 0, sizeof(*slot));
}

static int begin_slot(uint32_t frame, uint16_t frags, uint32_t need)
{
    if (s_qn >= RING_SLOTS || need == 0 || need > s_cap) {
        return -1;
    }
    if (s_qn == 0 && !s_partial) {
        s_head = 0;
        s_tail = 0;
        s_used = 0;
        s_wrapped = 0;
    }
    if (!s_wrapped && s_cap - s_tail < need) {
        if (s_head < need) {
            return -1;
        }
        s_tail = 0;
        s_wrapped = 1;
    }
    if (s_wrapped && (s_tail >= s_head || s_head - s_tail < need)) {
        return -1;
    }
    slot_t *slot = &s_q[s_qt];
    memset(slot, 0, sizeof(*slot));
    slot->offset = s_tail;
    slot->frame = frame;
    slot->frags = frags;
    s_partial = 1;
    return 0;
}

void ring_mount(uint8_t *base, uint32_t cap, uint8_t *scratch)
{
    s_base = base;
    s_cap = cap;
    s_scratch = scratch;
    s_mu = xSemaphoreCreateMutex();
    ring_clear();
}

void ring_clear(void)
{
    if (s_mu) {
        xSemaphoreTake(s_mu, portMAX_DELAY);
    }
    s_head = 0;
    s_tail = 0;
    s_used = 0;
    s_wrapped = 0;
    s_qh = 0;
    s_qt = 0;
    s_qn = 0;
    s_partial = 0;
    s_skip_key = 0;
    s_expect = 0;
    memset(s_q, 0, sizeof(s_q));
    atomic_store(&g.expect_seq, 0);
    if (s_mu) {
        xSemaphoreGive(s_mu);
    }
}

uint32_t ring_capacity(void)
{
    return s_cap;
}

uint32_t ring_free_bytes(void)
{
    xSemaphoreTake(s_mu, portMAX_DELAY);
    uint32_t room = tail_room();
    if (!s_partial && !s_wrapped && s_qn > 0 && s_head > room) {
        room = s_head;
    }
    if (s_partial) {
        slot_t *slot = &s_q[s_qt];
        uint32_t reserve = (uint32_t)(slot->frags - slot->got) * VIDEO_PAYLOAD;
        room = tail_room();
        room = room > reserve ? room - reserve : 0;
    }
    if (s_qn >= RING_SLOTS) {
        room = 0;
    }
    xSemaphoreGive(s_mu);
    return room;
}

static void note_expect(uint32_t seq)
{
    s_expect = seq + 1;
    atomic_store(&g.expect_seq, s_expect);
}

int ring_video(uint32_t seq, uint32_t frame, uint16_t frag, uint16_t frag_count, const uint8_t *payload, uint16_t len)
{
    if (len == 0 || len > VIDEO_PAYLOAD || frag_count == 0 || frag_count > 64 || frag >= frag_count) {
        return 1;
    }
    xSemaphoreTake(s_mu, portMAX_DELAY);
    int rc = 1;
    if (seq < s_expect) {
        goto out;
    }
    if (seq > s_expect) {
        rewind_partial();
        s_skip_key = 1;
        s_expect = seq;
    }
    if (s_skip_key) {
        if (frag != 0 || len < 2 || (payload[0] & 0x02) == 0) {
            note_expect(seq);
            goto out;
        }
        s_skip_key = 0;
    }
    if (!s_partial) {
        if (frag != 0) {
            s_skip_key = 1;
            note_expect(seq);
            goto out;
        }
        uint32_t need = (uint32_t)frag_count * VIDEO_PAYLOAD;
        if (need > ASM_BYTES) {
            need = ASM_BYTES;
        }
        if (begin_slot(frame, frag_count, need) != 0) {
            s_skip_key = 1;
            note_expect(seq);
            goto out;
        }
    }
    slot_t *slot = &s_q[s_qt];
    if (frame != slot->frame || frag != slot->got || frag_count != slot->frags) {
        rewind_partial();
        s_skip_key = 1;
        note_expect(seq);
        goto out;
    }
    if (tail_room() < len || slot->length + len > ASM_BYTES) {
        rewind_partial();
        s_skip_key = 1;
        note_expect(seq);
        goto out;
    }
    memcpy(s_base + s_tail, payload, len);
    s_tail += len;
    s_used += len;
    slot->length += len;
    slot->got++;
    if (!s_wrapped && s_tail == s_cap) {
        s_tail = 0;
        s_wrapped = 1;
    }
    note_expect(seq);
    if (slot->got == slot->frags) {
        slot->complete = 1;
        s_qt = (s_qt + 1) % RING_SLOTS;
        s_qn++;
        s_partial = 0;
    }
    rc = 0;
out:
    xSemaphoreGive(s_mu);
    return rc;
}

int ring_take(uint8_t **scratch, uint32_t *len, uint32_t *frame)
{
    xSemaphoreTake(s_mu, portMAX_DELAY);
    int ok = 0;
    if (s_qn > 0 && s_q[s_qh].complete) {
        slot_t slot = s_q[s_qh];
        memcpy(s_scratch, s_base + slot.offset, slot.length);
        s_used -= slot.length;
        s_qh = (s_qh + 1) % RING_SLOTS;
        s_qn--;
        if (s_qn == 0 && !s_partial) {
            s_head = 0;
            s_tail = 0;
            s_used = 0;
            s_wrapped = 0;
        } else if (s_qn > 0) {
            s_head = s_q[s_qh].offset;
            s_wrapped = s_tail < s_head;
        } else {
            s_head = s_q[s_qt].offset;
            s_wrapped = s_tail < s_head;
        }
        *scratch = s_scratch;
        *len = slot.length;
        *frame = slot.frame;
        ok = 1;
    }
    xSemaphoreGive(s_mu);
    return ok;
}
