#include "net.h"

#include <string.h>

#include "audio.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "lwip/sockets.h"
#include "nvs_flash.h"
#include "esp_timer.h"
#include "ring.h"
#include "state.h"
#include "wifi_secrets.h"

static const char *TAG = "w154";

#define SOCK_RCVBUF (32 * 1024)

static int s_video = -1;
static int s_audio = -1;
static int s_control = -1;
static struct sockaddr_in s_host;
static uint32_t s_audio_expect;
static int s_saw_control;

static void wifi_event(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    (void)arg;
    if (base == WIFI_EVENT && (id == WIFI_EVENT_STA_START || id == WIFI_EVENT_STA_DISCONNECTED)) {
        atomic_store(&g.have_ip, 0);
        esp_wifi_connect();
        return;
    }
    if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        const ip_event_got_ip_t *event = data;
        ESP_LOGI(TAG, "ip " IPSTR, IP2STR(&event->ip_info.ip));
        atomic_store(&g.have_ip, 1);
    }
}

static void wifi_start(void)
{
    esp_err_t nvs = nvs_flash_init();
    if (nvs == ESP_ERR_NVS_NO_FREE_PAGES || nvs == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        nvs = nvs_flash_init();
    }
    ESP_ERROR_CHECK(nvs);
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();
    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&cfg));
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, &wifi_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, &wifi_event, NULL));
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));

    wifi_config_t wifi = {0};
    strncpy((char *)wifi.sta.ssid, WIFI_SSID, sizeof(wifi.sta.ssid) - 1);
    strncpy((char *)wifi.sta.password, WIFI_PASS, sizeof(wifi.sta.password) - 1);
    wifi.sta.threshold.authmode = WIFI_AUTH_WPA2_PSK;
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wifi));
    ESP_ERROR_CHECK(esp_wifi_start());
    ESP_ERROR_CHECK(esp_wifi_set_ps(WIFI_PS_NONE));
}

static int bind_udp(int port)
{
    int sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0) {
        return -1;
    }
    int yes = 1;
    setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));
    int rcv = SOCK_RCVBUF;
    setsockopt(sock, SOL_SOCKET, SO_RCVBUF, &rcv, sizeof(rcv));
    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_port = htons((uint16_t)port);
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(sock, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
        close(sock);
        return -1;
    }
    return sock;
}

static void send_hello(void)
{
    uint8_t pkt[SESSION_BYTES] = {0};
    wr_u32(pkt, HELLO_MAGIC);
    wr_u16(pkt + 4, VIDEO_PORT);
    wr_u16(pkt + 6, AUDIO_PORT);
    wr_u16(pkt + 8, CONTROL_PORT);
    wr_u32(pkt + 12, ring_capacity());
    sendto(s_video, pkt, sizeof(pkt), 0, (struct sockaddr *)&s_host, sizeof(s_host));
}

static void send_credit(void)
{
    uint32_t flags = atomic_load(&g.flags);
    uint32_t packed = atomic_load(&g.play_frame) & CREDIT_FRAME_MASK;
    if (flags & PF_UNDERRUN) {
        packed |= CREDIT_FLAG_UNDERRUN;
    }
    if (flags & PF_PAUSE) {
        packed |= CREDIT_FLAG_PAUSED;
    }
    uint8_t pkt[SESSION_BYTES];
    wr_u32(pkt, CREDIT_MAGIC);
    wr_u32(pkt + 4, ring_free_bytes());
    wr_u32(pkt + 8, atomic_load(&g.expect_seq));
    wr_u32(pkt + 12, packed);
    sendto(s_video, pkt, sizeof(pkt), 0, (struct sockaddr *)&s_host, sizeof(s_host));
}

static void on_control(const uint8_t *pkt)
{
    uint32_t opcode = rd_u32(pkt + 4);
    uint32_t token = rd_u32(pkt + 8);
    uint32_t arg = rd_u32(pkt + 12);
    uint32_t cur = atomic_load(&g.token);
    if (opcode != OP_RESET && token != cur) {
        return;
    }
    s_saw_control = 1;
    if (opcode == OP_RESET) {
        player_on_reset(token, arg);
        s_audio_expect = 0;
        return;
    }
    if (opcode == OP_PAUSE) {
        player_on_pause();
    } else if (opcode == OP_RESUME) {
        player_on_resume();
    } else if (opcode == OP_AUDIO) {
        player_on_audio(arg);
    } else if (opcode == OP_EOS) {
        player_on_eos();
    }
}

static void on_video(const uint8_t *pkt, int n)
{
    if (n < SESSION_BYTES) {
        return;
    }
    uint16_t nbytes = rd_u16(pkt + 12);
    if (nbytes == 0 || nbytes > VIDEO_PAYLOAD || nbytes != (uint16_t)(n - SESSION_BYTES)) {
        return;
    }
    ring_video(rd_u32(pkt), rd_u32(pkt + 4), rd_u16(pkt + 8), rd_u16(pkt + 10), pkt + SESSION_BYTES, nbytes);
}

static void on_audio(const uint8_t *pkt, int n)
{
    if (n < SESSION_BYTES || atomic_load(&g.sample_rate) == 0) {
        return;
    }
    uint32_t seq = rd_u32(pkt);
    uint16_t nbytes = rd_u16(pkt + 8);
    if ((nbytes & 1u) != 0 || nbytes < 2 || nbytes != (uint16_t)(n - SESSION_BYTES)) {
        return;
    }
    if (seq < s_audio_expect) {
        return;
    }
    s_audio_expect = seq + 1;
    audio_push(pkt + SESSION_BYTES, nbytes);
}

void net_start(void)
{
    memset(&s_host, 0, sizeof(s_host));
    s_host.sin_family = AF_INET;
    s_host.sin_port = htons(HOST_HELLO_PORT);
    inet_aton(HOST_IP, &s_host.sin_addr);
    wifi_start();
}

void net_task(void *arg)
{
    (void)arg;
    while (!atomic_load(&g.have_ip)) {
        vTaskDelay(pdMS_TO_TICKS(100));
    }
    s_video = bind_udp(VIDEO_PORT);
    s_audio = bind_udp(AUDIO_PORT);
    s_control = bind_udp(CONTROL_PORT);
    if (s_video < 0 || s_audio < 0 || s_control < 0) {
        ESP_LOGE(TAG, "bind UDP falhou");
        vTaskDelete(NULL);
        return;
    }
    ESP_LOGI(TAG, "UDP video %d audio %d control %d -> %s:%d", VIDEO_PORT, AUDIO_PORT, CONTROL_PORT, HOST_IP,
             HOST_HELLO_PORT);
    send_hello();
    int64_t next_credit = esp_timer_get_time();
    int64_t next_hello = esp_timer_get_time() + 1000000;
    uint8_t buf[UDP_PACKET];
    for (;;) {
        fd_set set;
        FD_ZERO(&set);
        FD_SET(s_video, &set);
        FD_SET(s_audio, &set);
        FD_SET(s_control, &set);
        int maxfd = s_video;
        if (s_audio > maxfd) {
            maxfd = s_audio;
        }
        if (s_control > maxfd) {
            maxfd = s_control;
        }
        struct timeval tv = {.tv_sec = 0, .tv_usec = 20000};
        int ready = select(maxfd + 1, &set, NULL, NULL, &tv);
        if (ready > 0 && FD_ISSET(s_control, &set)) {
            int n = recv(s_control, buf, sizeof(buf), 0);
            if (n >= SESSION_BYTES && rd_u32(buf) == CONTROL_MAGIC) {
                on_control(buf);
            }
        }
        if (ready > 0 && FD_ISSET(s_video, &set)) {
            int n = recv(s_video, buf, sizeof(buf), 0);
            if (n > SESSION_BYTES) {
                on_video(buf, n);
            }
        }
        if (ready > 0 && FD_ISSET(s_audio, &set)) {
            int n = recv(s_audio, buf, sizeof(buf), 0);
            on_audio(buf, n);
        }
        int64_t now = esp_timer_get_time();
        if (now >= next_credit) {
            send_credit();
            next_credit = now + 20000;
        }
        if (now >= next_hello) {
            send_hello();
            next_hello = now + (s_saw_control ? 5000000 : 1000000);
        }
    }
}
