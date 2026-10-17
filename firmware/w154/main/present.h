#pragma once

#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"

void present_init(esp_lcd_panel_handle_t panel, esp_lcd_panel_io_handle_t io);
void present_reset(void);
void present_task(void *arg);
