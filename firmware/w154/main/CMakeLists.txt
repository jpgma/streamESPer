idf_component_register(
    SRCS "main.c" "net.c" "ring.c" "decode.c" "present.c" "audio.c"
    INCLUDE_DIRS "."
    REQUIRES esp_lcd esp_driver_gpio esp_driver_spi esp_driver_i2c esp_driver_i2s
             esp_timer esp_wifi esp_netif nvs_flash esp_event heap
)
