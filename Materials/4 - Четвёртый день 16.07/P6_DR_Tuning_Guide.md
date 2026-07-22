# 📝 Руководство к Практике 6: Логирование и анализ обучения с Domain Randomization

Этот документ посвящен анализу результатов обучения ИИ-агента с Domain Randomization (DR) с помощью сбора диагностических логов и работы с графиками TensorBoard. 

Скрипт `DiagnosticLogger.cs` и интеграцию его с `RobotBrain.cs` вам предстоит написать самостоятельно по приведенным ниже шагам.

---

## 🧐 Теория: Какую телеметрию собирать и зачем?

Чтобы понять, почему модель работает в симуляции, но ошибается в реальности, нам необходимо записывать полную телеметрию заезда. Ниже приведена таблица параметров, которые критически важно логировать на каждом шаге принятия решения, с объяснением их полезности для отладки:

| Категория логов | 📋 Конкретные параметры | 🧠 Зачем собирать и как это поможет в отладке |
| :--- | :--- | :--- |
| **Зрение (Камера)** | `ballSeen`, `ballAngle`, `ballDist` | **Помогает разделить ошибки зрения и управления**: если робот проехал мимо мяча, лог покажет, видел ли его датчик (`ballSeen=1`) и правильно ли оценивал угол. Если мяч не детектировался — проблема в камере/YOLO; если мяч был виден, но робот не повернул — проблема в модели RL. |
| **Физические сенсоры** | `ultrasonicDist`, `leftIR`, `rightIR`, `gripperIR` | **Анализ ложных срабатываний**: ИК-датчик клешни (`gripperIR`) должен срабатывать только у мяча. Если лог показывает регулярные единицы вдали от мяча — это ложные срабатывания (на ножки стола/пол), блокирующие движение. Сонар (`ultrasonicDist`) поможет понять, почему робот боится стен. |
| **Действия (Выходы ИИ)** | `gas`, `steering`, `cameraYawInput` | **Поиск «дребезга» и мертвых зон**: если графики `gas` и `steering` постоянно скачут между крайними значениями (например, $+1.0$ и $-1.0$) — модель совершает резкие движения, которые сожгут моторы. Также это помогает увидеть, попадают ли команды руления в мертвую зону привода робота. |
| **Внутреннее состояние** | `isRetrying`, `wasCloseToBall`, `hasBall` | **Отладка логических переходов**: позволяет отследить, корректно ли включается фаза сдачи назад (Retry) после потери мяча вблизи бампера, и останавливается ли робот сразу после захвата (`hasBall == true`). |
| **Одометрия и физика** | `displacementX`, `displacementZ`, `heading`, `speed` | **Построение траекторий**: по координатам смещения и скорости можно построить траекторию движения робота и оценить его плавность. |

---

## 💻 Шаг 1: Создание скрипта DiagnosticLogger.cs

Создайте новый C# скрипт **`DiagnosticLogger`** на вашем роботе в Unity. Этот класс будет отвечать за создание и запись CSV-файла в корневую папку вашего Unity-проекта.

### 📋 Структура полей и инициализация:
Определите следующие параметры для контроля логирования:

```csharp
using UnityEngine;
using System.IO;
using System.Text;
using System.Globalization; // Обязательно для InvariantCulture!

public class DiagnosticLogger : MonoBehaviour
{
    public bool enableLogging = false;  // Флаг включения записи логов
    public int logEveryN = 1;           // Записывать каждый N-й шаг (1 = каждый)
    public int maxRows = 2000;          // Ограничение размера файла (строк)

    private StreamWriter writer;
    private int rowsWritten = 0;
    private float startTime;

    void Start()
    {
        if (enableLogging)
        {
            // Путь к файлу: на один уровень выше папки Assets
            string path = Path.Combine(Application.dataPath, "..", "diagnostic_log.csv");
            
            // Открываем StreamWriter (false означает перезаписывать файл при каждом новом запуске)
            writer = new StreamWriter(path, false, Encoding.UTF8);
            
            // Записываем заголовок колонок CSV (строго в одну строчку без пробелов)
            writer.WriteLine("time,step,ballSeen,ballAngle,ballDist,uz,irL,irR,gripIR,camYaw,gas,steering,hasBall,holdTicks,isRetrying,displacementX,displacementZ,heading,speed");
            
            startTime = Time.time;
            Debug.Log($"[DiagnosticLogger] Запись лога запущена в: {path}");
        }
    }

    // Закрываем файл при уничтожении объекта (например, при выходе из игры)
    void OnDestroy()
    {
        writer?.Close();
    }
}
```

### 📋 Добавление метода записи шага (`LogStep`):
Добавьте метод, который преобразует входные значения от ИИ-агента в строку CSV и записывает её в файл, используя точку как разделитель дроби:

```csharp
public void LogStep(
    int step, bool ballSeen, float ballAngle, float ballDist,
    float uz, int irL, int irR, int gripIR, float camYaw,
    float gas, float steering, bool hasBall, int holdTicks, 
    bool isRetrying, float displacementX, float displacementZ, 
    float heading, float speed)
{
    // Защита от переполнения файла
    if (!enableLogging || writer == null || rowsWritten >= maxRows) return;

    float elapsed = Time.time - startTime;

    // Сборка строки с принудительным использованием CultureInfo.InvariantCulture
    string line = string.Format(CultureInfo.InvariantCulture,
        "{0:F3},{1},{2},{3:F4},{4:F4},{5:F4},{6},{7},{8},{9:F4},{10:F4},{11:F4},{12},{13},{14},{15:F4},{16:F4},{17:F4},{18:F4}",
        elapsed, step, ballSeen ? 1 : 0, ballAngle, ballDist, uz, irL, irR, gripIR, camYaw,
        gas, steering, hasBall ? 1 : 0, holdTicks, isRetrying ? 1 : 0, 
        displacementX, displacementZ, heading, speed);

    writer.WriteLine(line);
    writer.Flush(); // Сбрасываем буфер в файл, чтобы данные не пропали при сбоях
    rowsWritten++;

    if (rowsWritten >= maxRows)
    {
        Debug.Log($"[DiagnosticLogger] Сбор лога завершен. Достигнут лимит {maxRows} строк.");
    }
}
```

---

## 💻 Шаг 2: Интеграция с RobotBrain.cs

Откройте ваш скрипт **`RobotBrain.cs`** и подключите логгер:

1.  Объявите ссылку на логгер в полях класса:
    ```csharp
    private DiagnosticLogger diagLogger;
    ```
2.  Свяжите его в методе `Initialize()`:
    ```csharp
    diagLogger = GetComponent<DiagnosticLogger>();
    ```
3.  В самом конце метода **`OnActionReceived()`** (после всех расчетов скоростей и наград) вызовите метод логирования:
    ```csharp
    if (diagLogger != null)
    {
        // Передаем текущий шаг, состояние зрения, сенсоры, команды управления и одометрию
        diagLogger.LogStep(
            StepCount, 
            yoloCamera.seesBall, yoloCamera.normalizedAngle, yoloCamera.normalizedDistance,
            sensors.ultrasonicDist, sensors.leftIR, sensors.rightIR, sensors.gripperIR, 
            currentCameraYaw, gas, steering, gripper.hasBall, holdTicks, isRetrying,
            transform.position.x - startPosition.x, transform.position.z - startPosition.z,
            transform.eulerAngles.y / 360f, rb.linearVelocity.magnitude
        );
    }
    ```

---

## 📊 Шаг 3: Как анализировать логи для отладки Sim-to-Real

После тестового заезда откройте файл `diagnostic_log.csv` (он появится в корневой директории вашего проекта Unity) и постройте графики.

### 🔍 Ключевые паттерны поведения для анализа:

1.  **Связь дистанции и скорости (Динамическое торможение)**:
    *   *Что искать*: Постройте график `gas` (команда моторов) относительно `ballDist` (расстояние до мяча).
    *   *Правильное поведение*: По мере уменьшения `ballDist` (от 1.0 до 0.25) значение `gas` должно плавно падать (например, от `0.6` до `0.15`). Это говорит о том, что робот замедляется перед целью.
    *   *Аномалия*: Если график `gas` плоский (например, робот летит на стабильной скорости `0.4` до самого мяча) — модель собьет мяч в реальности.
2.  **Срабатывание фазы Retry (Отъезд назад)**:
    *   *Что искать*: Посмотрите на параметр `isRetrying` в моменты, когда `ballSeen` падает с `1` до `0` вблизи робота (`ballDist < 0.3`).
    *   *Правильное поведение*: Должна кратковременно включаться фаза `isRetrying == 1`, при которой робот сдает назад (отрицательный `gas`). Это спасает его, если он промахнулся.
3.  **Корреляция команд и мертвой зоны**:
    *   *Что искать*: Посмотрите, не выдает ли нейросеть слишком малые значения руления `steering` (например, `< 0.1` в тике).
    *   *Проблема*: В реальном коде Raspberry Pi маленькие команды отсекаются мертвой зоной моторов (`MIN_MOTOR_PWM`). Робот будет ехать прямо и не сможет центрироваться. В этом случае нужно скорректировать наградную функцию в Unity, штрафуя за бесполезные мелкие повороты, или увеличить коэффициент поворота на роботе.

---

## 📉 Шаг 4: Поиск аномалий в TensorBoard

При анализе графиков обучения с Domain Randomization обращайте внимание на следующие кривые:

*   **`Environment/Cumulative Reward`**: С Domain Randomization графики будут расти медленнее и иметь больше шума, чем в идеальном мире — это нормально, среда стала сложнее. Главное — отсутствие резких падений до отрицательных значений (свидетельствует о слишком больших штрафах за стены).
*   **`Policy/Entropy`**: Должна снижаться постепенно. Резкий обвал энтропии (например, до нуля на 300к шагов) говорит о том, что робот перестал исследовать мир и застрял в локальном минимуме (например, научился просто стоять у стены).
