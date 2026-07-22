using UnityEngine;
using System.IO;
using System.Text;
using System.Globalization;

// Практика 6: по-тиковый CSV-лог для offline-анализа sim-to-real расхождений.
// Чистый наблюдатель — только ЧИТАЕТ уже посчитанные значения и пишет в файл.
// НЕ трогает награду/наблюдения/действия агента, поэтому не может повлиять
// на то, чему учится сеть. Безопасно оставлять выключенным (enableLogging=false)
// на постоянной основе и включать точечно для конкретного диагностического прогона.
//
// ⚠️ АДАПТАЦИЯ ПОД НАШ ПРОЕКТ (важно): методичка рассчитана на ОДНОГО робота.
// У нас 16 арен работают параллельно в одном процессе — если бы все 16 экземпляров
// писали в один и тот же путь diagnostic_log.csv, StreamWriter'ы конфликтовали бы
// (гонка за файл, обрезание, возможен IOException). Поэтому имя файла включает
// имя корневого объекта арены — у каждого робота свой файл.
//
// ⚠️ ВКЛЮЧАТЬ ТОЛЬКО ДЛЯ ТОЧЕЧНОЙ ДИАГНОСТИКИ, не во время полного обучения на
// 16 арен: 16 одновременно пишущих файлов — это нормально (у каждого своё имя),
// но лог осмыслен только тогда, когда вы СМОТРИТЕ за конкретным роботом (Play/
// инференс с одной активной ареной), а не как побочный продукт тренировки.
public class DiagnosticLogger : MonoBehaviour
{
    [Header("Управление")]
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
            // Путь к файлу: на один уровень выше папки Assets, имя — с привязкой
            // к арене, чтобы 16 параллельных роботов не писали в один файл.
            string arenaTag = transform.root != null ? transform.root.name : gameObject.name;
            arenaTag = arenaTag.Replace(" ", "_").Replace("(", "").Replace(")", "");
            string fileName = $"diagnostic_log_{arenaTag}.csv";
            string path = Path.Combine(Application.dataPath, "..", fileName);

            // Открываем StreamWriter (false означает перезаписывать файл при каждом новом запуске)
            writer = new StreamWriter(path, false, Encoding.UTF8);

            // Записываем заголовок колонок CSV (строго в одну строчку без пробелов).
            // Примечание: ballAngle — НОРМАЛИЗОВАННЫЙ офсет -1..1 (не градусы),
            // как и остальные наблюдения агента (см. RobotBrain.CollectObservations).
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

    public void LogStep(
        int step, bool ballSeen, float ballAngle, float ballDist,
        float uz, int irL, int irR, int gripIR, float camYaw,
        float gas, float steering, bool hasBall, int holdTicks,
        bool isRetrying, float displacementX, float displacementZ,
        float heading, float speed)
    {
        // Защита от переполнения файла
        if (!enableLogging || writer == null || rowsWritten >= maxRows) return;

        // Прореживание: пишем только каждый logEveryN-й вызов
        if (logEveryN > 1 && step % logEveryN != 0) return;

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
}
