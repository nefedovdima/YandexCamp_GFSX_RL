using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Unity.MLAgents;

// Сбор статистики попыток робота GFS-X.
// Вешается на робота рядом с RobotBrain; ссылку на этот компонент кладём в поле
// RobotBrain.stats. RobotBrain вызывает RecordEpisode() в конце КАЖДОГО эпизода.
//
// Куда уходят данные:
//   1. Academy.StatsRecorder -> TensorBoard (вкладка "Attempt/*" и "Rewards/*").
//      Видно во время обучения: средний успех, средняя длительность, число захватов,
//      распределение компонентов награды. Пишется раз в summary_freq шагов.
//   2. Экранный оверлей (OnGUI) -> для визуального/ручного запуска (--interact):
//      сводка прямо в окне Play (успех %, среднее время, всего захватов).
//   3. Опциональный CSV -> по строке на попытку, для оффлайн-анализа
//      "распределения наград" и длительностей в Excel/pandas.
public class RobotStats : MonoBehaviour
{
    public enum Outcome { Success, Timeout, Fall }

    // Снимок результатов одной попытки. Заполняется в RobotBrain.EndAttempt().
    public struct EpisodeResult
    {
        public Outcome outcome;
        public float   durationSec;   // сколько длилась попытка, с
        public int     grabs;         // сколько раз реально взял мяч за попытку
        public int     wallHits;      // сколько раз врезался в стену за попытку
        public int     obstacleHits;  // сколько раз врезался в преграду за попытку
        public float   totalReward;   // суммарная награда за эпизод

        // Компоненты награды (для "распределения наград")
        public float rApproach;   // shaping сближения/центрирования
        public float rActionPen;  // штраф за резкость
        public float rTimePen;    // штраф за шаги (срочность)
        public float rObstProx;   // штраф за близость к преграде (ИК)
        public float rWallCol;    // штраф за столкновение со стеной
        public float rObstCol;    // штраф за столкновение с преградой
        public float rDirPen;     // штраф за направление (spin/forward/reverse)
        public float rGrab;       // помощь захвату (gripper_ir_reward минус grab_miss_penalty)
    }

    [Header("Экранная сводка")]
    [SerializeField] private bool showOverlay = true;      // рисовать сводку в окне Play
    [SerializeField] private Vector2 overlayOrigin = new Vector2(10f, 180f);

    [Header("Экспорт в CSV (опционально)")]
    [SerializeField] private bool  writeCsv = false;       // писать по строке на попытку
    [SerializeField] private string csvFileName = "";       // пусто = автоимя с меткой времени

    // ---------- Накопленные агрегаты (для оверлея) ----------
    private int   episodes;
    private int   successes, timeouts, falls;
    private int   totalGrabs;
    private int   totalWallHits;
    private float sumDuration;
    private float sumReward;
    private float lastReward;
    private float lastDuration;

    private StatsRecorder recorder;
    private string csvPath;
    private StreamWriter csv;

    void Awake()
    {
        // StatsRecorder есть только когда поднят Academy (обучение или инференс через
        // mlagents). В чистом редакторном Play без ML-Agents он тоже создаётся при
        // первом обращении к Academy.Instance, поэтому берём лениво в RecordEpisode.
        if (writeCsv) OpenCsv();
    }

    private void OpenCsv()
    {
        string name = string.IsNullOrEmpty(csvFileName)
            ? $"attempts_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv"
            : csvFileName;
        // Пишем рядом с проектом в папку StatsCSV (Application.dataPath = .../Assets)
        string dir = Path.Combine(Application.dataPath, "..", "StatsCSV");
        Directory.CreateDirectory(dir);
        csvPath = Path.Combine(dir, name);
        csv = new StreamWriter(csvPath, false, Encoding.UTF8);
        csv.WriteLine("episode,outcome,duration_sec,grabs,wall_hits,obstacle_hits,total_reward," +
                      "r_approach,r_action_pen,r_time_pen,r_obst_prox,r_wall_col,r_obst_col,r_dir_pen,r_grab");
        csv.Flush();
    }

    // Вызывается RobotBrain в конце каждой попытки.
    public void RecordEpisode(EpisodeResult r)
    {
        episodes++;
        switch (r.outcome)
        {
            case Outcome.Success: successes++; break;
            case Outcome.Timeout: timeouts++;  break;
            case Outcome.Fall:    falls++;     break;
        }
        totalGrabs    += r.grabs;
        totalWallHits += r.wallHits;
        sumDuration  += r.durationSec;
        sumReward    += r.totalReward;
        lastReward    = r.totalReward;
        lastDuration  = r.durationSec;

        PushToTensorBoard(r);
        WriteCsvRow(r);
    }

    private void PushToTensorBoard(EpisodeResult r)
    {
        if (recorder == null)
        {
            if (!Academy.IsInitialized) return;
            recorder = Academy.Instance.StatsRecorder;
            if (recorder == null) return;
        }

        // Attempt/* — сводные метрики попытки (усредняются за summary_freq).
        // Числовые префиксы задают ПОРЯДОК в TensorBoard (он сортирует по алфавиту):
        // сначала важное (очки, захваты, время), в конце — Fall (всегда 0, не важен).
        recorder.Add("Attempt/1_Reward",       r.totalReward);   // число очков
        recorder.Add("Attempt/2_Grabs",        r.grabs);         // число захватов мяча
        recorder.Add("Attempt/3_DurationSec",  r.durationSec);   // время попытки
        recorder.Add("Attempt/4_ObstacleHits", r.obstacleHits);  // удары в коробки/преграды
        recorder.Add("Attempt/5_WallHits",     r.wallHits);      // удары в стену
        recorder.Add("Attempt/6_Timeout",      r.outcome == Outcome.Timeout ? 1f : 0f);
        recorder.Add("Attempt/7_Success",      r.outcome == Outcome.Success ? 1f : 0f); // ~дублирует Grabs
        recorder.Add("Attempt/9_Fall",         r.outcome == Outcome.Fall    ? 1f : 0f); // всегда 0 (Y заморожен)

        // Rewards/* — распределение компонентов награды (среднее за окно)
        recorder.Add("Rewards/Approach",      r.rApproach);
        recorder.Add("Rewards/ActionPenalty", r.rActionPen);
        recorder.Add("Rewards/TimePenalty",   r.rTimePen);
        recorder.Add("Rewards/ObstacleProx",  r.rObstProx);
        recorder.Add("Rewards/WallCollision", r.rWallCol);
        recorder.Add("Rewards/ObstCollision", r.rObstCol);
        recorder.Add("Rewards/DirectionPen",  r.rDirPen);
        recorder.Add("Rewards/GrabHelp",      r.rGrab);
    }

    private void WriteCsvRow(EpisodeResult r)
    {
        if (csv == null) return;
        var ci = CultureInfo.InvariantCulture;
        csv.WriteLine(string.Join(",",
            episodes.ToString(ci),
            r.outcome.ToString(),
            r.durationSec.ToString("F3", ci),
            r.grabs.ToString(ci),
            r.wallHits.ToString(ci),
            r.obstacleHits.ToString(ci),
            r.totalReward.ToString("F4", ci),
            r.rApproach.ToString("F4", ci),
            r.rActionPen.ToString("F4", ci),
            r.rTimePen.ToString("F4", ci),
            r.rObstProx.ToString("F4", ci),
            r.rWallCol.ToString("F4", ci),
            r.rObstCol.ToString("F4", ci),
            r.rDirPen.ToString("F4", ci),
            r.rGrab.ToString("F4", ci)));
        csv.Flush();
    }

    void OnGUI()
    {
        if (!showOverlay) return;
        var s = new GUIStyle { fontSize = 15, normal = { textColor = Color.white } };

        float succRate = episodes > 0 ? 100f * successes / episodes : 0f;
        float avgDur   = episodes > 0 ? sumDuration / episodes : 0f;
        float avgRew   = episodes > 0 ? sumReward   / episodes : 0f;

        float x = overlayOrigin.x, y = overlayOrigin.y;
        GUI.Label(new Rect(x, y,      600, 20), $"Попыток: {episodes}   успех: {succRate:F0}%   (успех {successes} / таймаут {timeouts} / падение {falls})", s);
        GUI.Label(new Rect(x, y + 20, 600, 20), $"Всего захватов мяча: {totalGrabs}   ударов в стену: {totalWallHits}   ср. время попытки: {avgDur:F1} с", s);
        GUI.Label(new Rect(x, y + 40, 600, 20), $"Средняя награда: {avgRew:F2}   последняя: {lastReward:F2} за {lastDuration:F1} с", s);
    }

    void OnDisable()
    {
        if (csv != null)
        {
            csv.Flush();
            csv.Close();
            csv = null;
        }
    }
}
