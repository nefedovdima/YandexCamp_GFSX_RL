# 📝 Руководство к Практике 5: Внедрение Domain Randomization в Unity

> [!IMPORTANT]
> **Связь с Практикой 3**: Это руководство является прямым продолжением Практики 3. Мы будем модифицировать созданные вами ранее методы `CollectObservations()` и `OnActionReceived()` в скрипте `RobotBrain.cs`, чтобы внедрить в них шумы и задержки.

---

## 🧐 Теория: Зачем, Почему и Как работает Domain Randomization?

Перед тем как писать код, давайте разберем физику процессов, с которыми мы боремся. В таблице ниже описано, какие реальные проблемы мы решаем с помощью каждого шага рандомизации:

| Шаг рандомизации | ❓ ЗАЧЕМ (Какую проблему реальности решаем) | 🧠 ПОЧЕМУ (Принцип работы решения) | 🛠 КАК (Логика реализации в коде) |
| :--- | :--- | :--- | :--- |
| **Масса робота и параметры моторов** | Реальные роботы имеют разный вес (разные АКБ, крепления) и разное состояние редукторов (левый борт может ехать чуть быстрее правого). | Если обучить модель на одной массе, при переносе в реальность робот будет перелетать цель по инерции или буксовать. | В `OnEpisodeBegin()` мы случайно меняем массу Rigidbody и скорости/сглаживание в `TrackController` перед каждым стартом. |
| **Шум УЗ-дальномера (Sonar)** | Реальный датчик HC-SR04 «дрожит» и ошибается на несколько сантиметров из-за отражения звука от неровных стен. | Без шума ИИ-агент привыкнет к идеальным данным. Любое мелкое колебание датчика в реальности заставит его паниковать или стопориться. | В `CollectObservations()` мы подмешиваем случайное число (белый шум $\pm 5\%$) к значению дальномера. |
| **Пачечные потери (YOLO Burst Dropout)** | При быстрых поворотах камера робота смазывает изображение, и YOLO теряет мяч на $0.5..1.5$ секунды подряд. | Одиночные редкие пропуски кадров ИИ легко игнорирует. Нам нужно симулировать затяжную потерю цели именно в моменты резких разворотов. | Мы отслеживаем угловую скорость вращения робота и принудительно отключаем видимость мяча (`seesBall = false`) на $5..15$ шагов. |
| **Очередь задержек (Latency Queue)** | Команда скорости идет по Wi-Fi/ROS до робота с пингом $40..150$ мс. Робот реагирует на команды с опозданием. | Если не симулировать пинг, робот в реальности будет постоянно вилять зигзагами, пытаясь скорректировать курс с запозданием. | Мы пропускаем все команды нейросети через FIFO-буфер (`Queue`). Робот физически выполняет команду, принятую $8..13$ шагов назад. |

---

## 🗂 Пошаговая интеграция кода

### Шаг 1: Физическая рандомизация (В методе `OnEpisodeBegin`)
*   **Зачем**: Заставить ИИ-агента адаптироваться к разной динамике разгона и торможения.
*   **Почему именно так**: Динамическое изменение массы и параметров скрипта движения перед стартом каждого эпизода создает миллионы уникальных комбинаций поведения робота.
*   **Как сделать**: Откройте скрипт `RobotBrain.cs`, найдите метод `OnEpisodeBegin()` и вставьте туда код изменения массы робота и параметров привода:

```csharp
if (isTraining)
{
    if (rb != null)
    {
        // Рандомизируем массу робота от 1.0кг до 4.0кг (базовый вес 2.5кг)
        rb.mass = UnityEngine.Random.Range(1.0f, 4.0f);
    }

    if (track != null)
    {
        // Меняем динамические характеристики двигателей
        track.moveSpeed = UnityEngine.Random.Range(0.3f, 0.7f);   // базовый м/с +-40%
        track.turnSpeed = UnityEngine.Random.Range(80f, 160f);    // скорость вращения
        track.smoothing = UnityEngine.Random.Range(0.01f, 0.25f); // инерция привода
    }
}
```

*Для мяча в вашем методе сброса `ResetBall()` пропишите аналогичную логику изменения массы Rigidbody мяча ($\pm 100\%$) и его масштаба ($\pm 20\%$).*

---

### Шаг 2: Сенсорные шумы (В методе `CollectObservations`)
*   **Зачем**: Избежать переобучения сети на "идеально чистые" сигналы.
*   **Почему именно так**: Белый шум на ультразвуке учит ИИ доверять не конкретным миллиметрам, а общей тенденции сближения. Пачечный Dropout на камере учит использовать внутреннюю память (LSTM) для движения вслепую, когда мяч временно пропал из кадра.
*   **Как сделать**:
1. В начале класса `RobotBrain` объявите счетчик выпадения кадров YOLO:
   ```csharp
   private int burstDropoutRemaining = 0;
   ```
2. Откройте метод `CollectObservations` и перепишите логику считывания датчиков и камеры:

```csharp
public override void CollectObservations(VectorSensor sensor)
{
    if (sensors == null || yoloCamera == null || gripper == null) return;

    // 1. УЛЬТРАЗВУК С ШУМОМ
    // Подмешиваем случайную погрешность в пределах +-5% к нормализованной дистанции
    float noiseUS = isTraining ? UnityEngine.Random.Range(-0.05f, 0.05f) : 0f;
    float noisyDistance = Mathf.Clamp01(sensors.ultrasonicDist + noiseUS);
    sensor.AddObservation(noisyDistance); // 0 (УЗ дальномер с шумом)
    
    // Боковые ИК датчики оставляем бинарными (1 или 0)
    sensor.AddObservation(sensors.leftIR);  // 1
    sensor.AddObservation(sensors.rightIR); // 2
    sensor.AddObservation(sensors.gripperIR); // 3

    // 2. СИМУЛЯЦИЯ ПОТЕРЬ КАДРОВ YOLO (Burst Dropout)
    // Если счетчик активен — уменьшаем его на 1 за каждый физический тик
    if (burstDropoutRemaining > 0)
    {
        burstDropoutRemaining--;
    }
    // Если робот крутится на месте быстрее 0.5 рад/с — с шансом 15% активируем слепую зону на 5-15 шагов
    else if (isTraining && rb != null && rb.angularVelocity.magnitude > 0.5f)
    {
        if (UnityEngine.Random.value < 0.15f)
        {
            burstDropoutRemaining = UnityEngine.Random.Range(5, 16);
        }
    }
    
    bool yoloDropout = burstDropoutRemaining > 0;
    
    // Переопределяем видимость мяча с учетом симулированного лага камеры
    bool ballVisible = yoloCamera.seesBall && !yoloDropout;
    
    // Отправляем данные камеры в нейросеть
    sensor.AddObservation(ballVisible ? yoloCamera.normalizedAngle : 0f);  // 4 (угол до мяча)
    sensor.AddObservation(ballVisible ? yoloCamera.normalizedDistance : 1f); // 5 (дистанция до мяча)
    sensor.AddObservation(yoloCamera.lastKnownBallDirection);                       // 6
    sensor.AddObservation(ballVisible ? 1.0f : 0.0f);                       // 7
    sensor.AddObservation(yoloCamera.cameraYaw);                                    // 8
    
    // Оставшиеся наблюдения (состояние клешни, смещение, одометрия) отправляем без изменений
    sensor.AddObservation(gripper.hasBall ? 1.0f : 0.0f);                           // 9
    
    Vector3 displacement = transform.position - startPosition;
    sensor.AddObservation(Mathf.Clamp(displacement.x / 3f, -1f, 1f));               // 10
    sensor.AddObservation(Mathf.Clamp(displacement.z / 3f, -1f, 1f));               // 11
    sensor.AddObservation(transform.eulerAngles.y / 360f);                          // 12
    sensor.AddObservation(Mathf.Clamp01(rb.linearVelocity.magnitude / 0.5f));        // 13
    
    float timeSinceSeen = ballVisible ? 0f : 1f;
    sensor.AddObservation(timeSinceSeen);                                           // 14
}
```

---

### Шаг 3: Буфер задержки команд (В методе `OnActionReceived`)
*   **Зачем**: Сгладить виляние робота зигзагами в реальности из-за задержки Wi-Fi. Робот должен научиться подруливать плавно, заранее зная, что его команда применится с небольшим опозданием.
*   **Почему именно так**: Использование структуры данных `Queue` (очередь FIFO — первым пришел, первым ушел) идеально моделирует постоянный временной сдвиг сигналов.
*   **Как сделать**:
1. В начале класса `RobotBrain` объявите буфер очереди:
   ```csharp
   using System.Collections.Generic; // обязательно для работы очередей Queue

   private Queue<float[]> actionBuffer = new Queue<float[]>();
   private int currentActionLatency = 5;
   ```
2. В методе `OnEpisodeBegin()` инициализируйте буфер (заполняйте его нулями, имитируя, что команды еще не дошли до робота):
   ```csharp
   // Рандомизируем лаг от 8 до 13 шагов (160 - 260 мс) каждый эпизод обучения
   currentActionLatency = isTraining ? UnityEngine.Random.Range(8, 14) : 0;
   actionBuffer.Clear();
   for (int i = 0; i < currentActionLatency; i++)
   {
       actionBuffer.Enqueue(new float[] { 0f, 0f, 0f });
   }
   ```
3. Измените метод `OnActionReceived()` для пропуска сигналов через буфер:

```csharp
public override void OnActionReceived(ActionBuffers actions)
{
    if (gripper.hasBall)
    {
        track.Move(0f, 0f);
        holdTicks++;
        AddReward(0.02f);
        if (holdTicks >= 50)
        {
            AddReward(5.0f);
            EndEpisode();
        }
        return;
    }

    float gas, steering, cameraYawInput;

    if (isTraining && currentActionLatency > 0)
    {
        // Кладем свежее действие нейросети в конец очереди
        float[] newActions = new float[] {
            actions.ContinuousActions[0],
            actions.ContinuousActions[1],
            actions.ContinuousActions[2]
        };
        actionBuffer.Enqueue(newActions);

        // Достаем устаревшее на N шагов действие из начала очереди
        float[] delayed = actionBuffer.Dequeue();
        gas = delayed[0];
        steering = delayed[1];
        cameraYawInput = delayed[2];
    }
    else
    {
        // Без задержки (для ручного управления или инференса на реальном роботе)
        gas = actions.ContinuousActions[0];
        steering = actions.ContinuousActions[1];
        cameraYawInput = actions.ContinuousActions[2];
    }

    // Управляем двигателями с учетом задержки
    track.Move(gas, steering);
    yoloCamera.RotateCamera(cameraYawInput);

    // Управление клешней
    int gripperCmd = actions.DiscreteActions[0];
    if (gripperCmd == 1) gripper.CloseGripper();
    else if (gripperCmd == 2) gripper.OpenGripper();

    CalculateRewards(gas, steer);
}
```
