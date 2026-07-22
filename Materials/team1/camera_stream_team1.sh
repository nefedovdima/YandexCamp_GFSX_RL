#!/bin/bash
# Скрипт для запуска трансляции камеры (camera_stream_team1) на хосте Raspberry Pi

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )"
echo "=== Запуск camera_stream_team1 локально на Raspberry Pi ==="
python3 "$SCRIPT_DIR/camera_stream_team1.py"
