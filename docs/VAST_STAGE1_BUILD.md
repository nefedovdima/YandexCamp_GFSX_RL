# Воспроизводимая Vast.ai-сборка Stage 1

Эта инструкция описывает подготовку канонической training-сцены, проверку её контракта и сборку обычного Linux x86_64 Player. Runtime RL-код, reward, observations и actions в этом процессе не изменяются.

## Требования

- Unity `6000.3.19f1` с модулем Linux Build Support.
- Git checkout конкретного SHA, из которого должна собираться среда.
- Установленные зависимости проекта, включая `com.unity.ml-agents`.
- Чистая рабочая копия для воспроизводимой финальной сборки.

Перед сборкой проверьте:

```bash
git status --short
git rev-parse HEAD
```

При dirty working tree сборка разрешена, но Unity выведет предупреждение, а `BUILD_MANIFEST.txt` отметит, что один Git SHA недостаточен для воспроизведения.

## 1. Открытие проекта

Откройте корень проекта через Unity Hub в требуемой версии Unity. Дождитесь завершения импорта и компиляции. Не запускайте Play Mode.

Если Unity сообщает об ошибках компиляции, устраните их до подготовки сцены.

## 2. Подготовка канонической training-сцены

В Unity выберите:

`Tools > GFSX > Prepare Vast Training Scene`

Utility:

1. Открывает `Assets/Scenes/SampleScene.unity`.
2. Проверяет структуру 16 корневых арен `Arena` — `Arena (15)`.
3. Активирует арены и роботов.
4. На каждом роботе задаёт:
   - Behavior Type: `Default`;
   - Behavior Name: `GFSX_Brain`;
   - Model: `null`;
   - TeamId: `0`;
   - Decision Period: `5`;
   - Take Actions Between Decisions: `true`.
5. Проверяет, но не переписывает RL-контракт:
   - observations: `15`;
   - stacked vectors: `4`;
   - continuous actions: `3`;
   - одна discrete branch размера `3`.
6. Сохраняет настроенную копию как `Assets/Scenes/GFSX_Training.unity`.
7. Не сохраняет изменения в `SampleScene.unity` и не изменяет prefab `Arena`.

При первом создании сцены Unity автоматически создаст её `.meta`. Проверьте новую сцену и `.meta` перед будущим commit.

Если исходная сцена или другая открытая сцена имеет несохранённые изменения, utility остановится. Сохраните либо отмените их вручную и повторите команду.

## 3. Валидация training-сцены

Выберите:

`Tools > GFSX > Validate Vast Training Scene`

В Console появится подробный PASS/FAIL-отчёт. Валидатор проверяет:

- ровно 16 активных арен и 16 активных `RobotBrain`;
- уникальные world positions арен;
- ровно по одному `RobotBrain` в каждой арене;
- наличие собственных `EnvironmentRandomizer`, `Target_ball`, `VirtualSensors` и `SimulatedYoloCamera`;
- Behavior Type `Default`, Behavior Name `GFSX_Brain`, Model `null`, TeamId `0`;
- observation/action contract;
- Decision Period `5` и Take Actions Between Decisions `true`;
- отсутствие Heuristic Only и Inference Only;
- включение только `GFSX_Training.unity` в предполагаемый Vast build.

Любое нарушение даёт FAIL и блокирует Linux build.

## 4. Сборка Linux Player из меню

После PASS выберите:

`Tools > GFSX > Build Vast Linux Player`

Builder повторно запускает тот же валидатор и при успехе создаёт обычный Linux x86_64 Player:

```text
Build_Vast/GFSX_Training.x86_64
```

В build входит только:

```text
Assets/Scenes/GFSX_Training.unity
```

`SampleScene.unity` не включается. Dedicated Server subtarget и runtime headless-изменения не используются. На время сборки включается Run In Background, после чего исходное значение восстанавливается.

## 5. Command-line build из Windows PowerShell

Передайте путь к установленному `Unity.exe`, не записывая его в репозиторий:

```powershell
.\scripts\build_vast_linux.ps1 `
  -UnityEditorPath 'C:\Program Files\Unity\Hub\Editor\6000.3.19f1\Editor\Unity.exe'
```

Скрипт вызывает Unity с:

```text
-batchmode
-quit
-projectPath <project>
-executeMethod VastLinuxBuilder.BuildFromCommandLine
-logFile Build_Vast/build.log
```

При ошибке скрипт возвращает ненулевой exit code. Полный лог сохраняется в `Build_Vast/build.log`.

## 6. Проверка manifest

После успешной сборки откройте:

```text
Build_Vast/BUILD_MANIFEST.txt
```

Проверьте Git SHA, branch, состояние working tree, Unity/ML-Agents versions, timestamp, target, scene, executable и полный RL-контракт. Если Git недоступен, соответствующие поля имеют значение `UNKNOWN`.

Для воспроизводимого артефакта manifest должен содержать ожидаемый SHA и:

```text
Dirty working tree: FALSE
```

## 7. Архив и SHA-256

В Git Bash из корня проекта:

```bash
tar -czf GFSX_Training_linux.tar.gz Build_Vast config_stage1.yaml
sha256sum GFSX_Training_linux.tar.gz > GFSX_Training_linux.tar.gz.sha256
```

Проверьте checksum:

```bash
sha256sum -c GFSX_Training_linux.tar.gz.sha256
```

Архивы, build, log и generated manifest игнорируются Git.

## 8. Запуск benchmark на Linux/Vast.ai

После распаковки:

```bash
chmod +x Build_Vast/GFSX_Training.x86_64

mlagents-learn config_stage1.yaml \
  --env=Build_Vast/GFSX_Training.x86_64 \
  --run-id=gfsx_stage1_vast_01 \
  --num-envs=1 \
  --no-graphics \
  --time-scale=10 \
  --timeout-wait=300 \
  --max-lifetime-restarts=0
```

Первый запуск — короткий benchmark инфраструктуры, а не немедленное обучение на 500k steps. Сначала проверьте:

- успешное подключение Unity environment;
- отсутствие restart/timeout;
- ожидаемую скорость steps/sec;
- потребление CPU/GPU/RAM;
- запись checkpoints и TensorBoard summaries.

Только после успешного benchmark запускайте полный Stage 1.

## 9. Хранение результатов Vast.ai

Не храните единственную копию результатов на ephemeral storage инстанса. Настройте persistent volume либо регулярно копируйте наружу:

- `results/`;
- checkpoints;
- TensorBoard summaries;
- build archive и checksum;
- `BUILD_MANIFEST.txt`;
- `build.log`.

Перед остановкой или удалением инстанса убедитесь, что результаты доступны вне ephemeral disk.
