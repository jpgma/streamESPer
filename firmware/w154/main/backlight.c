#include "backlight.h"

#include "board_pins.h"
#include "driver/gpio.h"
#include "driver/ledc.h"
#include "esp_log.h"
#include "esp_sleep.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "net.h"

#include <stdatomic.h>

static const char *TAG = "w154";

#define BL_HZ 5000
#define BL_DUTY_MAX 1023
#define BL_FADE_MS 420
#define BL_SCAN_MS 20
#define BL_DARK_US 30000000LL

static esp_lcd_panel_handle_t s_panel;
static int s_on = 1;
static int s_armed;
static int s_down;
static int s_stable;
static int s_hold;
static atomic_int s_lit = 1;
static atomic_int s_want = 1;
static atomic_int s_dark;
static atomic_int s_sleep_due;
static esp_timer_handle_t s_dark_timer;
static portMUX_TYPE s_mux = portMUX_INITIALIZER_UNLOCKED;

int backlight_lit(void)
{
    return atomic_load(&s_lit);
}

static void fade_to(uint32_t target)
{
    uint32_t cur = ledc_get_duty(LEDC_LOW_SPEED_MODE, LEDC_CHANNEL_0);
    uint32_t delta = cur > target ? cur - target : target - cur;
    int ms;

    ledc_fade_stop(LEDC_LOW_SPEED_MODE, LEDC_CHANNEL_0);
    if (delta == 0) {
        return;
    }
    ms = (int)((delta * (uint32_t)BL_FADE_MS + BL_DUTY_MAX - 1) / BL_DUTY_MAX);
    ledc_set_fade_with_time(LEDC_LOW_SPEED_MODE, LEDC_CHANNEL_0, target, ms);
    ledc_fade_start(LEDC_LOW_SPEED_MODE, LEDC_CHANNEL_0, LEDC_FADE_NO_WAIT);
}

static void fade(int on)
{
    atomic_store(&s_want, on ? 1 : 0);
    if (on) {
        atomic_store(&s_lit, 1);
        atomic_store(&s_dark, 0);
    }
    fade_to(on ? BL_DUTY_MAX : 0);
}

static bool fade_end(const ledc_cb_param_t *param, void *user_arg)
{
    (void)user_arg;
    if (param->duty == 0 && atomic_load(&s_want) == 0) {
        atomic_store(&s_lit, 0);
        atomic_store(&s_dark, 1);
    }
    return false;
}

static void scan(void)
{
    int pressed;
    int on = 0;
    int edge = 0;

    portENTER_CRITICAL(&s_mux);
    if (s_hold) {
        portEXIT_CRITICAL(&s_mux);
        return;
    }
    pressed = gpio_get_level(PIN_PWR) == 0;
    if (!s_armed) {
        if (!pressed) {
            s_armed = 1;
        }
        portEXIT_CRITICAL(&s_mux);
        return;
    }
    if (!pressed) {
        s_stable = 0;
        s_down = 0;
        portEXIT_CRITICAL(&s_mux);
        return;
    }
    if (s_stable < 2) {
        s_stable++;
    }
    if (s_stable < 2 || s_down) {
        portEXIT_CRITICAL(&s_mux);
        return;
    }
    s_down = 1;
    s_on = !s_on;
    on = s_on;
    edge = 1;
    portEXIT_CRITICAL(&s_mux);
    if (edge) {
        fade(on);
        ESP_LOGI(TAG, "backlight %s", on ? "on" : "off");
    }
}

static void pins_hold(int hold)
{
    const gpio_num_t pins[] = {PIN_LCD_CS, PIN_LCD_CLK, PIN_LCD_MOSI, PIN_LCD_DC, PIN_LCD_RST};
    for (int i = 0; i < (int)(sizeof pins / sizeof pins[0]); i++) {
        if (hold) {
            gpio_sleep_sel_dis(pins[i]);
            gpio_hold_en(pins[i]);
        } else {
            gpio_hold_dis(pins[i]);
        }
    }
}

static void panel_resume(void)
{
    pins_hold(0);
    gpio_hold_dis(PIN_BAT_EN);
    if (!s_panel) {
        return;
    }
    esp_lcd_panel_init(s_panel);
    esp_lcd_panel_invert_color(s_panel, LCD_INVERT_COLOR);
    esp_lcd_panel_disp_on_off(s_panel, true);
    ESP_LOGI(TAG, "panel on");
}

static void pwr_ready(void)
{
    gpio_config_t key = {
        .pin_bit_mask = 1ull << PIN_PWR,
        .mode = GPIO_MODE_INPUT,
        .pull_up_en = GPIO_PULLUP_ENABLE,
    };
    gpio_config(&key);
}

static void swallow_press(void)
{
    portENTER_CRITICAL(&s_mux);
    s_on = 1;
    s_down = 1;
    s_stable = 2;
    s_hold = 0;
    s_armed = 1;
    portEXIT_CRITICAL(&s_mux);
}

static void host_sleep(void)
{
    while (gpio_get_level(PIN_PWR) == 0) {
        vTaskDelay(pdMS_TO_TICKS(20));
    }
    if (backlight_lit()) {
        return;
    }
    portENTER_CRITICAL(&s_mux);
    s_hold = 1;
    portEXIT_CRITICAL(&s_mux);
    esp_sleep_disable_wakeup_source(ESP_SLEEP_WAKEUP_ALL);
    if (esp_sleep_enable_ext0_wakeup(PIN_PWR, 0) != ESP_OK) {
        ESP_LOGW(TAG, "sleep skipped");
        swallow_press();
        fade(1);
        return;
    }
    ESP_LOGI(TAG, "light sleep");
    net_quiet();
    gpio_set_level(PIN_LCD_RST, 1);
    gpio_hold_en(PIN_BAT_EN);
    pins_hold(1);
    esp_light_sleep_start();
    panel_resume();
    pwr_ready();
    net_wake();
    swallow_press();
    fade(1);
    ESP_LOGI(TAG, "backlight on");
}

static void dark_timer_cb(void *arg)
{
    (void)arg;
    atomic_store(&s_sleep_due, 1);
}

static void bl_task(void *arg)
{
    int was_lit = 1;

    (void)arg;
    for (;;) {
        int lit;

        scan();
        lit = backlight_lit();
        if (lit) {
            if (!was_lit) {
                esp_timer_stop(s_dark_timer);
            }
        } else if (atomic_exchange(&s_dark, 0)) {
            esp_timer_stop(s_dark_timer);
            esp_timer_start_once(s_dark_timer, BL_DARK_US);
        }
        was_lit = lit;
        if (atomic_exchange(&s_sleep_due, 0) && !lit) {
            host_sleep();
            was_lit = backlight_lit();
        }
        vTaskDelay(pdMS_TO_TICKS(BL_SCAN_MS));
    }
}

void backlight_start(esp_lcd_panel_handle_t panel)
{
    ledc_timer_config_t timer = {
        .speed_mode = LEDC_LOW_SPEED_MODE,
        .duty_resolution = LEDC_TIMER_10_BIT,
        .timer_num = LEDC_TIMER_0,
        .freq_hz = BL_HZ,
        .clk_cfg = LEDC_AUTO_CLK,
    };
    ledc_channel_config_t channel = {
        .gpio_num = PIN_LCD_BL,
        .speed_mode = LEDC_LOW_SPEED_MODE,
        .channel = LEDC_CHANNEL_0,
        .timer_sel = LEDC_TIMER_0,
        .duty = 0,
        .hpoint = 0,
    };
    ledc_cbs_t cbs = {
        .fade_cb = fade_end,
    };
    const esp_timer_create_args_t dark = {
        .callback = dark_timer_cb,
        .name = "dark",
    };
    gpio_config_t key = {
        .pin_bit_mask = 1ull << PIN_PWR,
        .mode = GPIO_MODE_INPUT,
        .pull_up_en = GPIO_PULLUP_ENABLE,
    };

    s_panel = panel;
    ESP_ERROR_CHECK(ledc_timer_config(&timer));
    ESP_ERROR_CHECK(ledc_channel_config(&channel));
    ESP_ERROR_CHECK(ledc_fade_func_install(0));
    ESP_ERROR_CHECK(ledc_cb_register(LEDC_LOW_SPEED_MODE, LEDC_CHANNEL_0, &cbs, NULL));
    ESP_ERROR_CHECK(esp_timer_create(&dark, &s_dark_timer));
    ESP_ERROR_CHECK(gpio_config(&key));
    fade(1);
    ESP_ERROR_CHECK(xTaskCreatePinnedToCore(bl_task, "w154_bl", 4096, NULL, 1, NULL, 0) == pdPASS ? ESP_OK
                                                                                                    : ESP_FAIL);
}
