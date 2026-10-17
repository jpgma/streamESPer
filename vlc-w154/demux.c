// SPDX-License-Identifier: LGPL-2.1-or-later

/* vlc_threads.h calls poll(). This MinGW build has no poll.h. */
struct pollfd {
    int fd;
    short events;
    short revents;
};
int poll(struct pollfd *fds, unsigned nfds, int timeout);

#include <vlc_common.h>
#include <vlc_plugin.h>
#include <vlc_demux.h>
#include <vlc_aout.h>

#include "decode.h"

#include <stdlib.h>
#include <string.h>

#undef VLC_METADATA_EXPORTS
#define VLC_METADATA_EXPORTS \
    VLC_META_EXPORT(copyright, "Copyright (C) 2026 streamESPer") \
    VLC_META_EXPORT(license, VLC_LICENSE_LGPL_2_1_PLUS)

struct demux_sys_t {
    w154_file_info info;
    uint32_t *offsets;
    uint32_t next;
    w154_picture picture;
    es_out_id_t *video;
    es_out_id_t *audio;
    w154_audio_info audio_info;
    int has_audio;
    uint64_t pcm_offset;
    uint32_t audio_pos;
    uint8_t *body;
};

static int ReadExact(stream_t *stream, void *dst, size_t len)
{
    uint8_t *cursor = dst;
    while (len > 0) {
        ssize_t got = vlc_stream_Read(stream, cursor, len);
        if (got <= 0)
            return -1;
        cursor += got;
        len -= (size_t)got;
    }
    return 0;
}

static uint32_t ReadU32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint64_t Timeline(const demux_sys_t *sys)
{
    return (uint64_t)sys->info.frame_count * sys->info.frame_us;
}

static int DecodeFrame(demux_t *demux, uint32_t index)
{
    demux_sys_t *sys = demux->p_sys;
    if (vlc_stream_Seek(demux->s, sys->offsets[index]) != 0)
        return -1;
    uint8_t word[4];
    if (ReadExact(demux->s, word, 4) != 0)
        return -1;
    uint32_t body_len = ReadU32(word);
    if (body_len < W154_MIN_BODY || body_len > W154_MAX_BODY)
        return -1;
    if (ReadExact(demux->s, sys->body, body_len) != 0)
        return -1;
    if (w154_decode_body(sys->body, body_len, index, sys->info.gop, &sys->picture) != 0) {
        msg_Err(demux, "quadro %u invalido", index);
        return -1;
    }
    return 0;
}

static int SendAudio(demux_t *demux, uint32_t start, uint32_t end)
{
    demux_sys_t *sys = demux->p_sys;
    if (end <= start)
        return 0;

    uint32_t count = end - start;
    block_t *block = block_Alloc((size_t)count * 2u);
    if (block == NULL)
        return -1;

    uint64_t at = sys->pcm_offset + (uint64_t)start * 2u;
    if (vlc_stream_Seek(demux->s, at) != 0 || ReadExact(demux->s, block->p_buffer, (size_t)count * 2u) != 0) {
        block_Release(block);
        msg_Err(demux, "falha ao ler o PCM");
        return -1;
    }

    uint64_t pts_us = w154_sample_time(start, sys->audio_info.sample_rate);
    block->i_pts = VLC_TICK_0 + (vlc_tick_t)pts_us;
    block->i_dts = block->i_pts;
    block->i_length = (vlc_tick_t)w154_sample_time(count, sys->audio_info.sample_rate);
    block->i_nb_samples = count;
    es_out_Send(demux->out, sys->audio, block);
    return 0;
}

static int Demux(demux_t *demux)
{
    demux_sys_t *sys = demux->p_sys;
    if (sys->next >= sys->info.frame_count)
        return VLC_DEMUXER_EOF;

    uint32_t index = sys->next;
    if (DecodeFrame(demux, index) != 0)
        return VLC_DEMUXER_EGENERIC;

    uint64_t pts_us = (uint64_t)index * sys->info.frame_us;
    vlc_tick_t pts = VLC_TICK_0 + (vlc_tick_t)pts_us;
    es_out_SetPCR(demux->out, pts);

    block_t *video = block_Alloc((size_t)W154_PIXELS * 2u);
    if (video == NULL)
        return VLC_DEMUXER_EGENERIC;
    w154_expand_rgb565(&sys->picture, video->p_buffer);
    video->i_pts = pts;
    video->i_dts = pts;
    video->i_length = sys->info.frame_us;
    es_out_Send(demux->out, sys->video, video);

    if (sys->has_audio) {
        uint32_t end = index + 1 >= sys->info.frame_count
            ? sys->audio_info.sample_count
            : w154_first_sample(pts_us + sys->info.frame_us, sys->audio_info.sample_rate, sys->audio_info.sample_count);
        if (end < sys->audio_pos)
            end = sys->audio_pos;
        if (SendAudio(demux, sys->audio_pos, end) != 0)
            return VLC_DEMUXER_EGENERIC;
        sys->audio_pos = end;
    }

    sys->next = index + 1;
    return VLC_DEMUXER_SUCCESS;
}

static int PrimeTo(demux_t *demux, uint32_t target)
{
    demux_sys_t *sys = demux->p_sys;
    uint32_t key = target - (target % sys->info.gop);
    w154_picture *saved = malloc(sizeof(*saved));
    if (saved == NULL)
        return -1;
    *saved = sys->picture;
    sys->picture.ready = 0;
    for (uint32_t index = key; index < target; index++) {
        if (DecodeFrame(demux, index) != 0) {
            sys->picture = *saved;
            free(saved);
            return -1;
        }
    }
    free(saved);
    return 0;
}

static int SeekUs(demux_t *demux, uint64_t time_us)
{
    demux_sys_t *sys = demux->p_sys;
    uint64_t length = Timeline(sys);
    if (time_us >= length) {
        sys->next = sys->info.frame_count;
        if (sys->has_audio)
            sys->audio_pos = sys->audio_info.sample_count;
        return VLC_SUCCESS;
    }

    uint32_t frame = (uint32_t)(time_us / sys->info.frame_us);
    if (PrimeTo(demux, frame) != 0)
        return VLC_EGENERIC;
    sys->next = frame;
    if (sys->has_audio) {
        sys->audio_pos = w154_first_sample(
            (uint64_t)frame * sys->info.frame_us,
            sys->audio_info.sample_rate,
            sys->audio_info.sample_count);
    }
    return VLC_SUCCESS;
}

static int Control(demux_t *demux, int query, va_list args)
{
    demux_sys_t *sys = demux->p_sys;
    uint64_t length = Timeline(sys);
    uint64_t time = (uint64_t)sys->next * sys->info.frame_us;
    if (time > length)
        time = length;

    switch (query) {
    case DEMUX_CAN_SEEK:
    case DEMUX_CAN_PAUSE:
    case DEMUX_CAN_CONTROL_PACE:
        *va_arg(args, bool *) = true;
        return VLC_SUCCESS;
    case DEMUX_GET_LENGTH:
        *va_arg(args, vlc_tick_t *) = (vlc_tick_t)length;
        return VLC_SUCCESS;
    case DEMUX_GET_TIME:
        *va_arg(args, vlc_tick_t *) = (vlc_tick_t)time;
        return VLC_SUCCESS;
    case DEMUX_GET_POSITION:
        *va_arg(args, double *) = length == 0 ? 0.0 : (double)time / (double)length;
        return VLC_SUCCESS;
    case DEMUX_SET_POSITION: {
        double position = va_arg(args, double);
        if (position < 0.0)
            position = 0.0;
        if (position > 1.0)
            position = 1.0;
        return SeekUs(demux, (uint64_t)(position * (double)length));
    }
    case DEMUX_SET_TIME:
        return SeekUs(demux, (uint64_t)va_arg(args, vlc_tick_t));
    case DEMUX_SET_PAUSE_STATE:
        (void)va_arg(args, int);
        return VLC_SUCCESS;
    default:
        return VLC_EGENERIC;
    }
}

static void Close(vlc_object_t *object)
{
    demux_t *demux = (demux_t *)object;
    demux_sys_t *sys = demux->p_sys;
    if (sys == NULL)
        return;
    free(sys->offsets);
    free(sys->body);
    free(sys);
    demux->p_sys = NULL;
}

static int RejectAudio(demux_t *demux, stream_t *stream, uint64_t size, uint64_t cursor)
{
    demux_sys_t *sys = demux->p_sys;
    if (sys->info.audio_offset == 0)
        return cursor == size ? 0 : -1;
    if (cursor != sys->info.audio_offset)
        return -1;

    uint8_t preamble[16];
    if (ReadExact(stream, preamble, sizeof preamble) != 0)
        return -1;
    if (w154_parse_audio(preamble, sys->info.frame_count, sys->info.frame_us, &sys->audio_info) != 0)
        return -1;
    if (sys->info.audio_offset + 16ull + sys->audio_info.pcm_bytes != size)
        return -1;

    uint32_t crc = 0xFFFFFFFFu;
    uint32_t left = sys->audio_info.pcm_bytes;
    uint8_t chunk[8192];
    while (left > 0) {
        size_t step = left > sizeof chunk ? sizeof chunk : left;
        if (ReadExact(stream, chunk, step) != 0)
            return -1;
        crc = w154_crc32_update(crc, chunk, step);
        left -= (uint32_t)step;
    }
    if (w154_crc32_finish(crc) != sys->audio_info.crc32)
        return -1;

    sys->has_audio = 1;
    sys->pcm_offset = (uint64_t)sys->info.audio_offset + 16ull;
    return 0;
}

static int Open(vlc_object_t *object)
{
    demux_t *demux = (demux_t *)object;
    const uint8_t *peek = NULL;
    if (vlc_stream_Peek(demux->s, &peek, 4) < 4)
        return VLC_EGENERIC;
    if (peek[0] != 'W' || peek[1] != '1' || peek[2] != '5' || peek[3] != '4')
        return VLC_EGENERIC;

    bool can_seek = false;
    vlc_stream_Control(demux->s, STREAM_CAN_SEEK, &can_seek);
    if (!can_seek)
        return VLC_EGENERIC;

    uint64_t size = 0;
    if (vlc_stream_Control(demux->s, STREAM_GET_SIZE, &size) != VLC_SUCCESS || size < W154_HEADER_SIZE)
        return VLC_EGENERIC;

    demux_sys_t *sys = calloc(1, sizeof(*sys));
    if (sys == NULL)
        return VLC_ENOMEM;
    demux->p_sys = sys;

    uint8_t header[W154_HEADER_SIZE];
    if (vlc_stream_Seek(demux->s, 0) != 0 || ReadExact(demux->s, header, sizeof header) != 0
        || w154_parse_header(header, &sys->info) != 0) {
        Close(object);
        return VLC_EGENERIC;
    }
    if (sys->info.frame_count > (UINT32_MAX - 32u) / 4u
        || (uint64_t)sys->info.frame_count * 4u + 32u > size
        || (uint64_t)sys->info.frame_count * sys->info.frame_us > UINT64_MAX / 48000ull) {
        Close(object);
        return VLC_EGENERIC;
    }

    sys->offsets = calloc(sys->info.frame_count, sizeof(uint32_t));
    sys->body = malloc(W154_MAX_BODY);
    if (sys->offsets == NULL || sys->body == NULL) {
        Close(object);
        return VLC_ENOMEM;
    }

    for (uint32_t i = 0; i < sys->info.frame_count; i++) {
        uint8_t word[4];
        if (ReadExact(demux->s, word, 4) != 0) {
            Close(object);
            return VLC_EGENERIC;
        }
        sys->offsets[i] = ReadU32(word);
    }
    if (sys->offsets[0] != W154_HEADER_SIZE + sys->info.frame_count * 4u) {
        Close(object);
        return VLC_EGENERIC;
    }

    uint64_t cursor = sys->offsets[0];
    if (vlc_stream_Seek(demux->s, cursor) != 0) {
        Close(object);
        return VLC_EGENERIC;
    }
    for (uint32_t i = 0; i < sys->info.frame_count; i++) {
        if (cursor != sys->offsets[i]) {
            Close(object);
            return VLC_EGENERIC;
        }
        uint8_t word[4];
        if (ReadExact(demux->s, word, 4) != 0) {
            Close(object);
            return VLC_EGENERIC;
        }
        uint32_t body_len = ReadU32(word);
        if (body_len < W154_MIN_BODY || body_len > W154_MAX_BODY) {
            Close(object);
            return VLC_EGENERIC;
        }
        cursor += 4ull + body_len;
        if (i + 1 < sys->info.frame_count && cursor != sys->offsets[i + 1]) {
            Close(object);
            return VLC_EGENERIC;
        }
        if (vlc_stream_Seek(demux->s, cursor) != 0) {
            Close(object);
            return VLC_EGENERIC;
        }
    }
    if (RejectAudio(demux, demux->s, size, cursor) != 0) {
        msg_Err(demux, "arquivo w154 recusado");
        Close(object);
        return VLC_EGENERIC;
    }

    es_format_t video;
    es_format_Init(&video, VIDEO_ES, VLC_CODEC_RGB16);
    video.video.i_chroma = VLC_CODEC_RGB16;
    video.video.i_width = W154_WIDTH;
    video.video.i_height = W154_HEIGHT;
    video.video.i_visible_width = W154_WIDTH;
    video.video.i_visible_height = W154_HEIGHT;
    video.video.i_sar_num = 1;
    video.video.i_sar_den = 1;
    video.video.i_frame_rate = 1000000;
    video.video.i_frame_rate_base = sys->info.frame_us;
    video.video.i_bits_per_pixel = 16;
    video.video.i_rmask = 0xF800;
    video.video.i_gmask = 0x07E0;
    video.video.i_bmask = 0x001F;
    video.i_id = 1;
    sys->video = es_out_Add(demux->out, &video);
    es_format_Clean(&video);
    if (sys->video == NULL) {
        Close(object);
        return VLC_EGENERIC;
    }

    if (sys->has_audio) {
        es_format_t audio;
        es_format_Init(&audio, AUDIO_ES, VLC_CODEC_S16L);
        audio.audio.i_rate = sys->audio_info.sample_rate;
        audio.audio.i_physical_channels = AOUT_CHAN_CENTER;
        audio.audio.i_bitspersample = 16;
        audio.audio.i_channels = 1;
        aout_FormatPrepare(&audio.audio);
        audio.audio.i_blockalign = 2;
        audio.i_id = 2;
        sys->audio = es_out_Add(demux->out, &audio);
        es_format_Clean(&audio);
        if (sys->audio == NULL) {
            es_out_Del(demux->out, sys->video);
            Close(object);
            return VLC_EGENERIC;
        }
    }

    demux->pf_demux = Demux;
    demux->pf_control = Control;
    if (sys->has_audio)
        msg_Dbg(demux, "w154 aberto: %u quadros, audio %u Hz", sys->info.frame_count, sys->audio_info.sample_rate);
    else
        msg_Dbg(demux, "w154 aberto: %u quadros", sys->info.frame_count);
    return VLC_SUCCESS;
}

vlc_module_begin()
    set_shortname("w154")
    set_description("Waveshare 1.54 indexed video")
    set_capability("demux", 50)
    set_category(CAT_INPUT)
    set_subcategory(SUBCAT_INPUT_DEMUX)
    set_callbacks(Open, Close)
    add_shortcut("w154")
vlc_module_end()
