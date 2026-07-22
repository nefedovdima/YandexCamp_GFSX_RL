# 🧾 Отчёт о проделанной работе

**Дата:** 2026-07-17
**Проект:** GFS-X (Unity 6 + ML-Agents) — цифровой двойник робота-скаута.

Задачи сессии:
1. Поправить поведение агента («робот кружит на месте и не находит мяч»).
2. Вынести часто настраиваемые параметры в `config.yaml`, чтобы реже пересобирать билд.
3. Задокументировать конфиг.

---

## 1. Изменения в коде

### `Assets/Scripts/RobotBrain.cs`
Основной объём правок.

**Новые награды (борьба с верчением):**
- Добавлены поля `timePenalty` (штраф за каждый шаг — срочность) и `spinPenalty`
  (штраф за величину поворота).
- В `ComputeRewards()` начисляются два новых штрафа:
  `-timePenalty` каждый шаг и `-|steer| * spinPenalty`.
- **Причина:** старый `action_penalty` штрафовал только *изменение* руля, поэтому
  равномерное вращение `steer=1` было бесплатным и превращалось в устойчивую
  стратегию «покручусь — вдруг увижу мяч».

**Чтение параметров из `config.yaml`:**
- Добавлено поле `envParams` и его инициализация в `Initialize()`
  (`Academy.Instance.EnvironmentParameters`).
- Добавлен помощник `P(key, fallback)` — читает значение из `environment_parameters`,
  а если ключа нет — берёт значение из инспектора Unity.
- Добавлен метод `ApplyTunables()`, вызываемый первым в `OnEpisodeBegin()`:
  подтягивает веса наград, лимиты эпизода, скорость и ключевые амплитуды DR.

**Диапазоны Domain Randomization вынесены в поля инспектора:**
- Жёстко зашитые числа заменены на поля:
  `drMassRange`, `drMoveSpeedRange`, `drTurnSpeedRange`, `drPwmStepRange`,
  `drBallMassMul`, `drBallScaleMul`.
- Соответственно переписаны блок DR в `OnEpisodeBegin()` и метод `ResetBall()`.

### `Assets/Scripts/TrackController.cs`
- Добавлено свойство `MaxLinearCmd { get; set; }` — чтобы `RobotBrain` мог задавать
  лимит скорости из `config.yaml` (`max_linear_cmd`).

### `Assets/Scripts/EnvironmentRandomizer.cs`
- Добавлено поле `maxSpawnRadius` и свойство `MaxSpawnRadius`.
- В `SampleBallSpawn()` добавлено ограничение: при `maxSpawnRadius > 0` мяч не спавнится
  дальше заданного радиуса от робота.
- **Назначение:** рычаг сложности поиска (в т.ч. под будущий curriculum): мяч можно
  держать рядом на ранних этапах и отодвигать по мере обучения.

---

## 2. Изменения в конфигурации

### `config.yaml`
- Добавлена секция `environment_parameters` (плоский список, без curriculum).
- Значения по умолчанию соответствуют коду; отдельно:
  `episode_time_limit` поднят с 15 до **20 с** (дальние спавны раньше физически не
  успевались на скорости 0.25 м/с — структурный потолок).
- Блок `behaviors` (PPO) не менялся.

**Новые ключи `environment_parameters`:**
`approach_reward`, `centering_reward`, `action_penalty`, `obstacle_penalty`,
`grab_reward`, `fall_penalty`, `timeout_penalty`, `hold_tick_reward`,
`time_penalty`, `spin_penalty`, `episode_time_limit`, `hold_ticks_required`,
`max_linear_cmd`, `ball_spawn_jitter`, `ball_spawn_radius`, `dr_enabled`,
`us_noise`, `yolo_dropout_chance`, `latency_min`, `latency_max`,
`mass_min`, `mass_max`.

---

## 3. Новые файлы документации

- **`CONFIG_REFERENCE.md`** — подробное описание каждого параметра `config.yaml`
  (и PPO, и `environment_parameters`), с направлением тюнинга и таблицей типовых
  сценариев.
- **`WORK_REPORT.md`** — этот отчёт.

---

## 4. Прочее (по ходу сессии, вне кода)

- **Диагностика запуска обучения в Yandex Cloud.** Причина ошибки
  `UnityTimeOutException` — билд собран как *Dedicated Server*, и нативная библиотека
  `libgrpc_csharp_ext.x64.so` оказалась в папке `Plugins/AnyCPU/`, которую gRPC не
  сканирует. Быстрое решение (без пересборки): скопировать её в `Plugins/`.
  Долгосрочное — собирать как обычный Standalone Linux.
- **Удалён** `Materials/.ssh/public_key.pub` (для чистоты; восстановим из `yc_key`
  командой `ssh-keygen -y -f yc_key` при необходимости).

---

## 5. Что нужно сделать перед следующим обучением

> ⚠️ Правки — это изменения **C#-кода**, поэтому билд надо **пересобрать один раз**.
> После этого значения в `environment_parameters` меняются без пересборки.

**Чек-лист в Unity:**
1. Открыть проект, дождаться компиляции, убедиться, что в Console **нет ошибок**.
2. (Опционально) проверить новые поля в инспекторе `RobotBrain`:
   `Time Penalty`, `Spin Penalty`, диапазоны `Dr ... Range`.
3. (Опционально) проверить поле `Max Spawn Radius` на компоненте
   `EnvironmentRandomizer`.
4. Пересобрать билд (**Standalone Linux**, не Dedicated Server — см. п.4).
5. Запустить новый прогон с `--run-id` нового имени (не перетирать старые результаты).

**Быстрая проверка эффекта анти-спина:** в первые ~200k шагов средняя награда не
должна стагнировать на верчении; если робот всё ещё кружит — поднять в `config.yaml`
`spin_penalty` до `0.006` и `time_penalty` до `0.002` (пересборка не требуется).

---

## 6. Затронутые файлы (сводка)

| Файл | Тип изменения |
| :--- | :--- |
| `Assets/Scripts/RobotBrain.cs` | правка (награды + чтение config + поля DR) |
| `Assets/Scripts/TrackController.cs` | правка (свойство `MaxLinearCmd`) |
| `Assets/Scripts/EnvironmentRandomizer.cs` | правка (радиус спавна мяча) |
| `config.yaml` | правка (секция `environment_parameters`, `episode_time_limit`) |
| `CONFIG_REFERENCE.md` | новый файл |
| `WORK_REPORT.md` | новый файл |
| `Materials/.ssh/public_key.pub` | удалён |
