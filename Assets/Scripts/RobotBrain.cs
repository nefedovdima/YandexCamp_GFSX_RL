using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Мозг робота GFS-X. Собирает 15 наблюдений, принимает 3 непрерывных
// действия + 1 дискретное, передаёт их в TrackController/GripperController,
// считает награды и решает, когда заканчивать эпизод.
[RequireComponent(typeof(Rigidbody))]
public class RobotBrain : Agent
{
    [Header("Ссылки на тело робота")]
    [SerializeField] private TrackController   track;
    [SerializeField] private GripperController gripper;
    [SerializeField] private VirtualSensors    sensors;
    [SerializeField] private SimulatedYoloCamera cam;
    [SerializeField] private Transform         eye;   // объект-"глаз" (сервопривод камеры)
    [SerializeField] private ROSBridge         rosBridge; // мост в ROS; работает только на инференсе

    [Header("Сервопривод камеры")]
    [SerializeField] private float camServoMaxAngle = 60f;  // предел поворота, градусы
    [SerializeField] private float camServoSpeed     = 90f;  // скорость поворота, град/сек

    [Header("Условия падения")]
    [SerializeField] private float fallY = -1f; // ниже этой высоты Y считаем, что робот упал с арены

    [Header("Сброс мяча")]
    [SerializeField] private float ballSpawnJitter = 0.15f; // случайный сдвиг мяча от стартовой точки в начале эпизода, м (0 = всегда на одном месте)
    [SerializeField, Range(0f, 1f)] private float hiddenSpawnFraction = 0.5f; // доля эпизодов, где мяч гарантированно НЕ виден со старта (за стеной/вне кадра) — заставляет учиться искать

    [Header("Спавн робота")]
    [SerializeField] private float robotSpawnRadius = 0.25f; // "радиус" корпуса робота для проверки, что точка спавна не внутри коробки/стены

    [Header("Диагностика (Практика 6, опционально)")]
    // DiagnosticLogger ищется автоматически на этом же объекте (GetComponent в Initialize).
    // Если компонент не добавлен или enableLogging=false на нём — логирование не работает,
    // на обучение это никак не влияет (чистый наблюдатель).
    [SerializeField] private float retryDetectDist  = 0.3f; // порог "мяч был близко" для эвристики isRetrying (только для лога)
    [SerializeField] private int   retryDetectTicks = 25;    // сколько тиков после потери держим isRetrying=true

    [Header("Domain Randomization (Практика 5)")]
    [SerializeField] private bool enableDomainRandomization = true; // мастер-выключатель всех шумов/задержек
    [SerializeField] private bool randomizeArenaInPlay      = true; // рандомизировать поле/мяч и в визуальном/ручном запуске (--interact), когда тренер не подключён
    [SerializeField] private EnvironmentRandomizer environment;     // коробки/свет/трение/спавн мяча (опционально)
    [SerializeField] private float usNoise          = 0.05f;  // белый шум УЗ-дальномера, ±доля от нормализованного значения
    [SerializeField] private float irFlipChance     = 0.02f;  // шанс ложного срабатывания/пропуска бокового ИК за тик
    [SerializeField] private float dropoutYawRate   = 30f;    // порог скорости поворота (град/с), выше которого YOLO может "ослепнуть"
    [SerializeField] private float dropoutChance    = 0.15f;  // шанс включить слепую зону на каждом тике быстрого поворота
    [SerializeField] private float baseDropoutChance = 0.01f; // шанс случайной потери YOLO за тик даже без поворота
    [SerializeField] private int   dropoutMinSteps  = 5;      // мин. длительность слепоты, тиков
    [SerializeField] private int   dropoutMaxSteps  = 15;     // макс. длительность слепоты, тиков
    [SerializeField] private int   latencyMinSteps  = 8;      // мин. задержка команд, тиков (8 тиков = 160 мс)
    [SerializeField] private int   latencyMaxSteps  = 13;     // макс. задержка команд, тиков (260 мс)
    [SerializeField] private Vector2 drMassRange      = new Vector2(1.0f, 4.0f);  // разброс массы робота, кг
    [SerializeField] private Vector2 drMoveSpeedRange = new Vector2(0.3f, 0.7f);  // разброс базовой скорости, м/с
    [SerializeField] private Vector2 drTurnSpeedRange = new Vector2(80f, 160f);   // разброс скорости поворота, град/с
    [SerializeField] private Vector2 drPwmStepRange   = new Vector2(8f, 25f);     // разброс инерции разгона
    [SerializeField] private Vector2 drBallMassMul    = new Vector2(0.5f, 2f);    // множитель массы мяча
    [SerializeField] private Vector2 drBallScaleMul   = new Vector2(0.8f, 1.2f);  // множитель размера мяча

    [Header("Domain Randomization: одометрия (obs 11/12/13)")]
    // Реальный робот НЕ знает точных координат — только одометрию (энкодеры + IMU),
    // а она дрейфует. В симе смещение/курс идеальны, поэтому зашумляем их при обучении,
    // иначе политика привыкнет доверять точной позиции и на железе "поплывёт".
    [SerializeField] private float odomDrift        = 0.03f; // накопительный дрейф позиции, м (std за ~1 с, растёт ~sqrt(t))
    [SerializeField] private float odomNoise        = 0.01f; // белый шум позиции за тик, м
    [SerializeField] private float odomScaleErr     = 0.05f; // ошибка масштаба смещения (±доля) на эпизод
    [SerializeField] private float odomHeadingDrift = 3f;    // дрейф курса, град (std за ~1 с, растёт ~sqrt(t))

    [Header("Удержание мяча (терминальное условие)")]
    [SerializeField] private int   holdTicksRequired = 50;    // сколько тиков стабильно держать мяч для успеха (~1 сек)
    [SerializeField] private float holdTickReward    = 0.02f; // награда за каждый тик удержания

    [Header("Помощь захвату (чтобы чаще брал мяч)")]
    // gripperIrReward тянет робота в саму позу захвата: платит, пока мяч уже в луче
    // клешни (gripperIR=1), но ещё не схвачен. Это «последний метр», где обычным
    // shaping'ом сигнал слабый. grabMissPenalty штрафует «щёлканье» клешнёй вхолостую.
    [SerializeField] private float gripperIrReward = 0.02f;   // награда/тик, пока мяч в луче клешни и не схвачен
    [SerializeField] private float grabMissPenalty = 0.0f;    // штраф за команду "закрыть" без мяча в клешне (0 = выкл)
    [SerializeField] private float searchFindReward = 0.5f;   // РАЗОВЫЙ бонус за первое обнаружение мяча в эпизоде — учит искать скрытый со старта мяч
    [SerializeField] private float ballVisibleReward = 0.005f; // награда/тик, пока мяч виден в камере — единственный прямой стимул для сервопривода камеры активно следить за мячом. ОБЯЗАН быть < time_penalty, иначе выгодно зависать вместо захвата (тот же баг, что чинили для gripper_ir_reward)

    [Header("Лимит времени эпизода")]
    [SerializeField] private float episodeTimeLimit = 90f;   // секунд на попытку (1.5 минуты)
    [SerializeField] private float timeoutPenalty    = -0.3f; // штраф за истечение времени без результата

    [Header("Столкновения (нужны слои стен/преград — см. LayerMask ниже)")]
    // Пол в сцене тоже лежит на слое Obstacle, поэтому «штраф за любой контакт» наказывал
    // бы робота за езду по полу. Решение: (1) считаем только контакты с почти горизонтальной
    // нормалью (вертикальная нормаль = пол/потолок — игнор), (2) сам контакт должен попасть
    // в назначенную маску стен или преград. Стены и коробки желательно развести по разным
    // слоям (напр. отдельный слой Wall) — тогда штрафы будут раздельными.
    [SerializeField] private LayerMask wallCollisionMask     = default;    // слой(и) стен — назначь в инспекторе (напр. Wall)
    [SerializeField] private LayerMask obstacleCollisionMask = default;    // слой(и) коробок/преград — назначь в инспекторе (напр. Obstacle)
    [SerializeField] private float wallCollisionPenalty     = 0.05f;    // штраф/тик касания стены
    [SerializeField] private float obstacleCollisionPenalty = 0.03f;    // штраф/тик касания преграды
    [SerializeField] private float collisionFloorNormalY    = 0.7f;     // |normal.y| выше порога = пол, контакт игнорируем

    [Header("Веса наград (подбираются экспериментально)")]
    // ВАЖНО: approach/centering — это ВЕСА ПОТЕНЦИАЛА Ф(s), а не доход за тик.
    // Награда за шаг считается как gamma*Ф(s') - Ф(s) (potential-based shaping).
    // Такая форма математически не даёт накрутить награду circling'ом: любой
    // замкнутый маршрут даёт в сумме ~0, платит только фактический прогресс.
    [SerializeField] private float approachRewardScale  = 1.0f;   // вес близости к мячу в Ф(s)
    [SerializeField] private float centeringRewardScale = 0.5f;   // вес «мяч в кадре и по центру» в Ф(s)
    [SerializeField] private float actionPenaltyScale   = 0.01f;  // штраф за резкость управления
    [SerializeField] private float obstaclePenalty      = 0.01f;  // штраф за близость к стене (ИК)
    [SerializeField] private float grabReward           = 5.0f;   // бонус за успешный захват
    [SerializeField] private float fallPenalty          = -1.0f;  // штраф за падение с арены
    [SerializeField] private float timePenalty          = 0.001f; // штраф за каждый шаг (ургентность)
    [SerializeField] private float spinPenalty          = 0.0f;   // штраф за |поворот| (движение "в сторону"). 0 = выкл: при узком FOV робот ОБЯЗАН крутиться, чтобы искать мяч
    [SerializeField] private float forwardPenalty       = 0.0f;   // штраф за газ вперёд (обычно 0; включать не нужно, движение вперёд полезно)
    [SerializeField] private float reversePenalty       = 0.0f;   // штраф за газ назад (камера и клешня спереди; реверс полезен только чтобы выпутаться)
    [SerializeField] private float shapingGamma         = 0.99f;  // должен совпадать с gamma из config.yaml

    [Header("Статистика попыток (опционально)")]
    [SerializeField] private RobotStats stats;   // сбор метрик: захваты, длительность, распределение наград

    private Rigidbody rb;
    private Vector3   startPosition;       // заводская точка спавна (Awake) — фолбэк, если рандомизация выключена/не нашла место
    private Vector3   episodeStartPosition; // ФАКТИЧЕСКАЯ точка спавна ЭТОГО эпизода — от неё считается одометрия (obs 11-12).
                                             // Реальный робот обнуляет одометрию в начале каждой попытки, а не в фиксированной
                                             // точке карты, поэтому при случайном спавне отсчёт обязан переезжать вместе с ним.
    private Quaternion startRotation;

    private Quaternion eyeStartLocalRot;  // заводской поворот "глаза" (камера смотрит вперёд и чуть вниз)
    private Transform  ball;              // трансформ мяча (берём из SimulatedYoloCamera)
    private Rigidbody  ballRb;
    private Collider   ballCol;
    private Vector3    ballStartPosition; // стартовая точка мяча на арене

    private float prevPotential;       // Ф(s) на предыдущем шаге (potential-based shaping)
    private float prevGas, prevSteer, prevCam;  // предыдущие команды (для штрафа за резкость)
    private float camServoAngle;       // текущий угол сервопривода камеры, -max..+max
    private float episodeTimer;        // сколько секунд длится текущий эпизод

    // ---------- Состояние Domain Randomization ----------
    private bool  isTraining;                 // подключён ли внешний тренер (mlagents-learn)
    private float robotBaseMass;              // заводская масса робота (для восстановления при инференсе)
    private float ballBaseMass;               // заводская масса мяча
    private Vector3 ballBaseScale;            // заводской масштаб мяча
    private int   burstDropoutRemaining;      // сколько тиков камера ещё "слепа" (YOLO Burst Dropout)
    private float prevYaw;                    // угол рыскания на прошлом тике (для оценки скорости поворота)
    private float timeSinceBallSeen;          // свой таймер "давности" мяча с учётом dropout
    private readonly Queue<float[]> actionBuffer = new Queue<float[]>(); // FIFO-очередь задержки команд
    private int   currentActionLatency;       // задержка этого эпизода, тиков
    private int   holdTicks;                  // сколько тиков подряд мяч удерживается

    // ---------- Столкновения (обновляются в OnCollision*, гасятся в ComputeRewards) ----------
    private bool  wallTouch;                   // в этом решении был контакт со стеной
    private bool  obstacleTouch;               // в этом решении был контакт с преградой
    private int   wallHitCount;                // сколько раз врезался в стену за эпизод
    private int   obstacleHitCount;            // сколько раз врезался в преграду за эпизод

    private bool  everSeenBall;                // видел ли робот мяч хоть раз в этом эпизоде (для разового бонуса поиска)

    // ---------- Состояние шума одометрии (per-episode) ----------
    private float   odomScale;                 // масштабная ошибка смещения этого эпизода
    private Vector2 odomDriftAccum;            // накопленный дрейф позиции (X,Z), random walk
    private float   odomHeadingDriftAccum;     // накопленный дрейф курса, град

    // ---------- Учёт статистики за попытку ----------
    private bool  prevHolding;                 // держал ли мяч на прошлом тике (детект момента захвата)
    private int   grabCount;                   // сколько раз за эпизод реально взял мяч
    private float rApproach, rActionPen, rTimePen, rObstProx, rWallCol, rObstCol, rDirPen, rGrab; // суммы компонентов награды за эпизод

    private EnvironmentParameters envParams;  // значения из config.yaml (environment_parameters)

    // ---------- Практика 6: диагностический лог (опционально, только наблюдатель) ----------
    private DiagnosticLogger diagLogger;
    private bool wasCloseAndVisible;   // мяч был виден и близко на прошлом тике (для эвристики isRetrying)
    private int  retryFlagTicksLeft;   // сколько тиков ещё показывать isRetrying=true в логе

    // ---------- Жизненный цикл Agent ----------

    // ВАЖНО: базовый Agent.Awake() (protected internal virtual) регистрирует ML-Agents-
    // коммуникатор. Свой void Awake() его СКРЫВАЛ (warning CS0114) — тогда базовая
    // инициализация не выполнялась. Правильно: override + base.Awake() первым вызовом
    // (ровно как в примере из документации Agent.cs).
    protected override void Awake()
    {
        base.Awake();

        // Запоминаем стартовые позицию/поворот один раз, до первого OnEpisodeBegin
        startPosition = transform.position;
        episodeStartPosition = startPosition; // safety default до первого OnEpisodeBegin
        startRotation = transform.rotation;

        // Базовый поворот камеры задан в сцене (~89° по Y + наклон вниз, потому что
        // перёд робота — это ось +X). Сервопривод должен крутить ОТНОСИТЕЛЬНО него,
        // а не затирать его нулями.
        if (eye != null)
            eyeStartLocalRot = eye.localRotation;

        if (cam != null && cam.TargetBall != null)
        {
            ball    = cam.TargetBall;
            ballRb  = ball.GetComponent<Rigidbody>();
            ballCol = ball.GetComponent<Collider>();
            ballStartPosition = ball.position;
            ballBaseScale     = ball.localScale;
            if (ballRb != null) ballBaseMass = ballRb.mass;
        }
    }

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        robotBaseMass = rb.mass;
        diagLogger = GetComponent<DiagnosticLogger>(); // опционально; если компонента нет - остаётся null, лог просто не пишется

        // Тренер подключён -> это обучение, включаем рандомизацию.
        // Инференс на модели или ручной режим -> всё чисто, без шумов и задержек.
        isTraining = Academy.Instance.IsCommunicatorOn;

        // Параметры из config.yaml (секция environment_parameters). Позволяют крутить
        // веса наград, лимиты и разброс DR БЕЗ пересборки билда — правишь yaml и перезапускаешь.
        envParams = Academy.Instance.EnvironmentParameters;

        // С этого момента TrackController и GripperController слушают агента, а не клавиатуру
        if (track   != null) track.ExternalControl   = true;
        if (gripper != null) gripper.ExternalControl = true;

        // Если маски столкновений не назначены в инспекторе — по умолчанию считаем
        // преградой всё на слое Obstacle (коробки/стены сцены). От пола нас защищает
        // проверка нормали (см. ClassifyContact), а не маска, поэтому это безопасно.
        // Развести стены и коробки по РАЗНЫМ штрафам можно, назначив маски вручную.
        if (wallCollisionMask.value == 0 && obstacleCollisionMask.value == 0)
        {
            int obstacleLayer = LayerMask.NameToLayer("Obstacle");
            if (obstacleLayer >= 0) obstacleCollisionMask = 1 << obstacleLayer;
        }
    }

    // Рандомизация физики/сенсоров (масса, шум УЗ, задержки) — только на обучении.
    private bool ApplyDR => isTraining && enableDomainRandomization;

    // Рандомизация САМОЙ АРЕНЫ и точки спавна мяча: на обучении — всегда, вне обучения
    // (визуальный/ручной запуск, --interact) — если включён флаг randomizeArenaInPlay.
    private bool ApplyArenaRandomization
        => (isTraining && enableDomainRandomization) || (!isTraining && randomizeArenaInPlay);

    // Читает параметр из config.yaml (environment_parameters). Если его там нет —
    // берётся значение из инспектора Unity (fallback). Ключи см. в environment_parameters.
    private float P(string key, float fallback)
        => envParams != null ? envParams.GetWithDefault(key, fallback) : fallback;

    // Подтягивает настраиваемые параметры из config.yaml в начале каждого эпизода.
    // Вызывается первым в OnEpisodeBegin. Так значения можно менять на лету (в т.ч.
    // curriculum'ом), не пересобирая Unity-билд.
    private void ApplyTunables()
    {
        if (envParams == null) return;

        // --- веса наград ---
        approachRewardScale  = P("approach_reward",  approachRewardScale);
        centeringRewardScale = P("centering_reward", centeringRewardScale);
        actionPenaltyScale   = P("action_penalty",   actionPenaltyScale);
        obstaclePenalty      = P("obstacle_penalty", obstaclePenalty);
        grabReward           = P("grab_reward",      grabReward);
        gripperIrReward      = P("gripper_ir_reward", gripperIrReward);
        grabMissPenalty      = P("grab_miss_penalty", grabMissPenalty);
        searchFindReward     = P("search_find_reward", searchFindReward);
        ballVisibleReward    = P("ball_visible_reward", ballVisibleReward);
        fallPenalty          = P("fall_penalty",     fallPenalty);
        timeoutPenalty       = P("timeout_penalty",  timeoutPenalty);
        holdTickReward       = P("hold_tick_reward", holdTickReward);
        timePenalty          = P("time_penalty",     timePenalty);
        spinPenalty          = P("spin_penalty",     spinPenalty);
        forwardPenalty       = P("forward_penalty",  forwardPenalty);
        reversePenalty       = P("reverse_penalty",  reversePenalty);
        wallCollisionPenalty     = P("wall_collision_penalty",     wallCollisionPenalty);
        obstacleCollisionPenalty = P("obstacle_collision_penalty", obstacleCollisionPenalty);
        shapingGamma         = P("shaping_gamma",    shapingGamma);

        // --- эпизод ---
        episodeTimeLimit  = P("episode_time_limit", episodeTimeLimit);
        holdTicksRequired = Mathf.RoundToInt(P("hold_ticks_required", holdTicksRequired));

        // --- движение ---
        if (track != null) track.MaxLinearCmd = P("max_linear_cmd", track.MaxLinearCmd);

        // --- спавн мяча / сложность поиска (для curriculum) ---
        ballSpawnJitter     = P("ball_spawn_jitter",      ballSpawnJitter);
        hiddenSpawnFraction = P("hidden_spawn_fraction",  hiddenSpawnFraction);
        if (environment != null)
        {
            environment.MaxSpawnRadius = P("ball_spawn_radius", environment.MaxSpawnRadius);

            // Число коробок-преград из yaml. box_count_max: 0 -> преград нет.
            int bmin = Mathf.RoundToInt(P("box_count_min", environment.ActiveBoxCount.x));
            int bmax = Mathf.RoundToInt(P("box_count_max", environment.ActiveBoxCount.y));
            environment.ActiveBoxCount = new Vector2Int(bmin, bmax);
        }

        // --- Domain Randomization: мастер-выключатель и ключевые амплитуды ---
        enableDomainRandomization = P("dr_enabled", enableDomainRandomization ? 1f : 0f) > 0.5f;
        usNoise         = P("us_noise",            usNoise);
        odomDrift       = P("odom_drift",          odomDrift);
        odomNoise       = P("odom_noise",          odomNoise);
        dropoutChance   = P("yolo_dropout_chance", dropoutChance);
        latencyMinSteps = Mathf.RoundToInt(P("latency_min", latencyMinSteps));
        latencyMaxSteps = Mathf.RoundToInt(P("latency_max", latencyMaxSteps));
        drMassRange = new Vector2(P("mass_min", drMassRange.x), P("mass_max", drMassRange.y));
    }

    public override void OnEpisodeBegin()
    {
        // Подтягиваем настройки из config.yaml (веса наград, лимиты, разброс DR).
        // Первым делом — до ApplyDR и ResetBall, т.к. они читают эти значения.
        ApplyTunables();

        // ВАЖНО: сначала отпускаем мяч, и только потом телепортируем робота.
        // Иначе мяч (дочерний объект HoldPoint) уедет на спавн вместе с роботом.
        if (gripper != null && gripper.IsHolding)
            gripper.ReleaseBall();

        // Спавн робота: случайная точка арены (вне стен/коробок) при обучении/интеракте,
        // иначе заводская точка. ВАЖНО: считаем ДО RandomizeBoxes() — тогда коробки сами
        // расставятся вокруг новой точки через существующий robotClearRadius.
        Vector3 spawnPos = startPosition;
        Quaternion spawnRot = startRotation;
        if (ApplyArenaRandomization && environment != null)
        {
            Vector3? spot = environment.SampleRobotSpawn(robotSpawnRadius, startPosition.y);
            if (spot.HasValue)
            {
                spawnPos = spot.Value;
                spawnRot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            }
        }
        transform.position = spawnPos;
        transform.rotation = spawnRot;
        rb.linearVelocity  = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        episodeStartPosition = spawnPos; // отсюда считаем одометрию (obs 11-12) ЭТОГО эпизода

        // Перестраиваем арену: коробки, свет, трение (до спавна мяча,
        // чтобы мяч не оказался внутри свежепоставленной коробки).
        // На обучении — всегда; в визуальном/ручном запуске — если включён
        // randomizeArenaInPlay (тогда поле рандомизируется и при --interact).
        if (environment != null)
        {
            if (ApplyArenaRandomization) environment.Randomize();
            else                         environment.RestoreDefaults();
        }

        // Сброс камеры в стартовую позу ДО ResetBall: спавн мяча проверяет видимость
        // именно с этой позы (для режима "мяч за стеной"), поэтому камера уже должна
        // смотреть куда положено, а не под углом из прошлого эпизода.
        camServoAngle = 0f;
        if (eye != null) eye.localRotation = eyeStartLocalRot;

        // Возвращаем мяч: при обучении — случайная точка арены (в т.ч. за коробками),
        // иначе — стартовая точка с небольшим сдвигом
        ResetBall();

        prevGas   = 0f;
        prevSteer = 0f;
        prevCam   = 0f;
        episodeTimer = 0f;
        holdTicks    = 0;

        // Сброс счётчиков столкновений и статистики попытки
        wallTouch = obstacleTouch = false;
        wallHitCount = obstacleHitCount = 0;
        wasCloseAndVisible = false;
        retryFlagTicksLeft = 0;
        prevHolding = false;
        grabCount   = 0;
        rApproach = rActionPen = rTimePen = rObstProx = rWallCol = rObstCol = rDirPen = rGrab = 0f;

        // Базовый уровень потенциала. Обязательно ПОСЛЕ ResetBall и телепорта робота,
        // иначе первый же шаг эпизода получит фиктивный скачок награды.
        // Камеру пересчитываем принудительно: её FixedUpdate в этом кадре ещё не был.
        if (cam != null) cam.Refresh();
        prevPotential = CurrentPotential();

        // Если мяч уже виден на старте — поиск не нужен, бонус за обнаружение не даём.
        // Если скрыт — everSeenBall станет true в тот момент, когда робот его найдёт.
        everSeenBall = cam != null && cam.BallVisible;

        // ---------- Шаг 1 DR: физическая рандомизация ----------
        // Каждый эпизод робот "другой": иная масса, иная динамика моторов.
        // Так политика учится не под один экземпляр робота, а под весь разброс
        // реальных машин (просевший АКБ, разболтанный редуктор и т.д.).
        if (ApplyDR)
        {
            rb.mass = Random.Range(drMassRange.x, drMassRange.y);   // базовый вес 2.5 кг ± разброс

            if (track != null)
            {
                track.MoveSpeed  = Random.Range(drMoveSpeedRange.x, drMoveSpeedRange.y); // базовая 0.57 м/с ± ~40%
                track.TurnSpeed  = Random.Range(drTurnSpeedRange.x, drTurnSpeedRange.y); // базовая 120 град/с
                track.MaxPwmStep = Random.Range(drPwmStepRange.x,   drPwmStepRange.y);   // инерция разгона
            }
        }
        else
        {
            rb.mass = robotBaseMass; // на инференсе возвращаем заводские параметры
        }

        // ---------- Шаг 3 DR: очередь задержки команд (Latency Queue) ----------
        // Реальные команды идут через Wi-Fi/ROS с пингом 160-260 мс. Заполняем FIFO-буфер
        // нулями ("команды ещё не дошли"), и робот будет исполнять приказы с опозданием.
        currentActionLatency = ApplyDR ? Random.Range(latencyMinSteps, latencyMaxSteps + 1) : 0;
        actionBuffer.Clear();
        for (int i = 0; i < currentActionLatency; i++)
            actionBuffer.Enqueue(new float[] { 0f, 0f, 0f });

        // Сброс сенсорных шумов
        burstDropoutRemaining = 0;
        timeSinceBallSeen     = 0f;
        prevYaw               = transform.eulerAngles.y;

        // Сброс шума одометрии: новая масштабная ошибка на эпизод, дрейф с нуля
        odomScale             = ApplyDR ? 1f + Random.Range(-odomScaleErr, odomScaleErr) : 1f;
        odomDriftAccum        = Vector2.zero;
        odomHeadingDriftAccum = 0f;
    }

    // Возвращает мяч на стартовую позицию и восстанавливает его физику
    private void ResetBall()
    {
        if (ball == null) return;

        // При обучении с рандомизатором мяч появляется в случайной точке арены —
        // в том числе вне поля зрения и за коробками. Иначе — у стартовой точки.
        Vector3 pos = ballStartPosition;
        bool spawned = false;
        if (ApplyArenaRandomization && environment != null)
        {
            float ballRadius = ballBaseScale.x * 0.6f; // с запасом на увеличенный DR-масштаб

            // С вероятностью hiddenSpawnFraction требуем, чтобы мяч НЕ был виден со старта
            // (за стеной/коробкой или вне узкого кадра камеры) — это заставляет робота
            // учиться искать, а не только ехать по прямой к видимой цели.
            bool wantHidden = cam != null && Random.value < hiddenSpawnFraction;
            System.Func<Vector3, bool> reject = wantHidden
                ? (System.Func<Vector3, bool>)(p => cam.WouldSeeBallAt(p))
                : null;

            Vector3? spawn = environment.SampleBallSpawn(ballRadius, ballStartPosition.y, reject);
            // Если скрытую точку за отведённые попытки не нашли — берём любую (не срываем эпизод)
            if (!spawn.HasValue && wantHidden)
                spawn = environment.SampleBallSpawn(ballRadius, ballStartPosition.y, null);

            if (spawn.HasValue)
            {
                pos = spawn.Value;
                spawned = true;
            }
        }
        if (!spawned && ballSpawnJitter > 0f)
        {
            // Запасной вариант: если случайная точка не нашлась — хотя бы сдвигаем,
            // чтобы мяч никогда не "замерзал" на одном месте
            Vector2 j = Random.insideUnitCircle * ballSpawnJitter;
            pos += new Vector3(j.x, 0f, j.y);
        }

        ball.SetParent(null);
        ball.position = pos;
        ball.rotation = Quaternion.identity;

        // DR: каждый эпизод мяч немного другой — размер ±20%, масса x0.5..x2.
        // Реальные мячи различаются, а YOLO выдаёт рамки разного размера.
        ball.localScale = ApplyDR
            ? ballBaseScale * Random.Range(drBallScaleMul.x, drBallScaleMul.y)
            : ballBaseScale;

        // Страховка: если захват прервался нештатно, физика мяча могла остаться выключенной
        if (ballCol != null) ballCol.enabled = true;
        if (ballRb != null)
        {
            ballRb.isKinematic     = false;
            ballRb.linearVelocity  = Vector3.zero;
            ballRb.angularVelocity = Vector3.zero;
            ballRb.mass = ApplyDR
                ? ballBaseMass * Random.Range(drBallMassMul.x, drBallMassMul.y)
                : ballBaseMass;
        }
    }

    // ---------- Шаг 3.1: наблюдения (ровно 15, порядок как в методичке) ----------
    // При обучении сюда подмешиваются шумы Domain Randomization (Практика 5):
    // белый шум УЗ и пачечные потери кадров YOLO. На инференсе данные чистые.
    public override void CollectObservations(VectorSensor sensor)
    {
        // ---------- Защита от "взрыва" физики ----------
        // Редкий, но неизбежный на масштабе многих миллионов шагов баг движка: при столкновении
        // с малой (DR-рандомизированной) массой Rigidbody иногда улетает в Infinity/NaN.
        // Испорченная позиция иначе протекает в наблюдения (обс. 11-13, 5-6 через дистанцию
        // до мяча) -> Python роняет ВЕСЬ тренировочный процесс с "observations had Infinite
        // values". Ловим на корню: если позиция робота или мяча не конечна - не считаем
        // наблюдения из мусора, шлём нейтральный вектор и немедленно сбрасываем эпизод.
        bool robotBroken = !IsFiniteVector(transform.position) || !IsFiniteVector(rb.linearVelocity)
                         || !float.IsFinite(camServoAngle)
                         || !float.IsFinite(odomDriftAccum.x) || !float.IsFinite(odomDriftAccum.y)
                         || !float.IsFinite(odomHeadingDriftAccum);
        bool ballBroken   = ball != null && !IsFiniteVector(ball.position);
        if (robotBroken || ballBroken)
        {
            for (int i = 0; i < 15; i++) sensor.AddObservation(0f); // держим контракт Space Size = 15
            Debug.LogWarning($"[RobotBrain] Обнаружена нефинитная позиция (robot={robotBroken}, ball={ballBroken}) - аварийный сброс эпизода.");
            AddReward(fallPenalty);
            EndAttempt(RobotStats.Outcome.Fall);
            return;
        }

        // 1. УЗ-дальномер (0 вплотную .. 1 чисто) + белый шум ±5%.
        //    Реальный HC-SR04 "дрожит" на пару сантиметров из-за переотражений звука.
        //    Шум учит сеть доверять тенденции сближения, а не конкретным миллиметрам.
        float us = sensors != null ? sensors.Ultrasonic : 1f;
        if (ApplyDR) us = Mathf.Clamp01(us + Random.Range(-usNoise, usNoise));
        sensor.AddObservation(us);
        // 2-3. Боковые ИК-датчики (0/1) + редкие ложные срабатывания/пропуски.
        //      Реальные ИК-модули иногда "мигают" от помех и бликов — сеть не должна
        //      паниковать от одиночного неверного бита.
        float leftIR  = sensors != null ? sensors.LeftIR  : 0f;
        float rightIR = sensors != null ? sensors.RightIR : 0f;
        if (ApplyDR && Random.value < irFlipChance) leftIR  = 1f - leftIR;
        if (ApplyDR && Random.value < irFlipChance) rightIR = 1f - rightIR;
        sensor.AddObservation(leftIR);
        sensor.AddObservation(rightIR);
        // 4. ИК-датчик клешни (0/1) — оставляем чистым: он физически управляет захватом
        sensor.AddObservation(sensors != null ? sensors.GripperIR : 0f);

        // ---------- Шаг 2 DR: пачечные потери YOLO (Burst Dropout) ----------
        // При быстром развороте картинка смазывается и YOLO теряет мяч на 0.1-0.3 с.
        // Скорость поворота меряем по дельте угла (rb.angularVelocity у нас всегда ~0,
        // т.к. робот вращается через MoveRotation).
        float yaw = transform.eulerAngles.y;
        float yawRateDeg = Mathf.Abs(Mathf.DeltaAngle(prevYaw, yaw)) / Time.fixedDeltaTime;
        prevYaw = yaw;

        if (burstDropoutRemaining > 0)
        {
            burstDropoutRemaining--;
        }
        else if (ApplyDR)
        {
            if (yawRateDeg > dropoutYawRate && Random.value < dropoutChance)
            {
                // Длинная слепота при резком развороте (смаз изображения)
                burstDropoutRemaining = Random.Range(dropoutMinSteps, dropoutMaxSteps + 1);
            }
            else if (Random.value < baseDropoutChance)
            {
                // Короткие случайные потери: YOLO иногда просто мигает даже на месте
                burstDropoutRemaining = Random.Range(3, 9);
            }
        }

        // Эффективная видимость мяча = реальная видимость МИНУС симулированная слепота
        bool ballVisible = cam != null && cam.BallVisible && burstDropoutRemaining == 0;

        // Свой таймер давности детекции (таймер камеры не знает про dropout)
        timeSinceBallSeen = ballVisible ? 0f : timeSinceBallSeen + Time.fixedDeltaTime;

        // 5. Относительный горизонтальный угол до мяча ПО КАДРУ КАМЕРЫ (0, если мяч не виден).
        //    Система отсчёта — текущий поворот камеры, а не корпуса: нужен, чтобы сеть
        //    понимала, куда довернуть САМУ КАМЕРУ (сервопривод), а не корпус.
        sensor.AddObservation(ballVisible ? cam.HorizontalOffset : 0f);
        // 6. Нормализованное расстояние до мяча по камере (1, если мяч не виден)
        sensor.AddObservation(ballVisible ? cam.NormalizedDistance : 1f);
        // 7. Последнее известное направление на мяч (память после утери из кадра).
        //    ОТКАТ: пробовали заменить на пересчитанный пеленг относительно корпуса
        //    (camServoAngle + офсет), знак формулы подтверждён верным эмпирически,
        //    но два прогона подряд (train24 resume и train25 с нуля) не дали
        //    устойчивого прироста относительно этой, проверенной версии — откатили,
        //    т.к. нужен гарантированный рабочий результат, а не теоретически более
        //    правильная, но пока не окупившаяся правка.
        sensor.AddObservation(cam != null ? cam.LastKnownOffset : 0f);
        // 8. Флаг видимости мяча
        sensor.AddObservation(ballVisible ? 1f : 0f);
        // 9. Текущий поворот сервопривода камеры, нормализованный -1..1
        sensor.AddObservation(camServoMaxAngle > 0f ? camServoAngle / camServoMaxAngle : 0f);
        // 10. Статус захвата мяча клешнёй
        sensor.AddObservation(gripper != null && gripper.IsHolding ? 1f : 0f);
        // 11-12. Относительное смещение робота от точки старта (X, Z), нормализовано к ±3 м.
        //     ЭТО ОДОМЕТРИЯ: у реального робота она неточная (энкодеры+IMU дрейфуют).
        //     При обучении подмешиваем ошибку масштаба + накопительный дрейф + шум,
        //     чтобы политика не привыкала к идеальным координатам симулятора.
        Vector3 delta = transform.position - episodeStartPosition;
        float odomX = delta.x, odomZ = delta.z;
        // 13. Курс: перёд робота — transform.right, сравниваем с мировой осью X (-180..180).
        float heading = Vector3.SignedAngle(Vector3.right, transform.right, Vector3.up);

        if (ApplyDR)
        {
            // Накопительный дрейф (random walk): std растёт ~sqrt(t) — как у реальной одометрии.
            float sdt = Mathf.Sqrt(Time.fixedDeltaTime);
            odomDriftAccum.x       += RandGaussian() * odomDrift        * sdt;
            odomDriftAccum.y       += RandGaussian() * odomDrift        * sdt;
            odomHeadingDriftAccum  += RandGaussian() * odomHeadingDrift * sdt;

            odomX   = delta.x * odomScale + odomDriftAccum.x + Random.Range(-odomNoise, odomNoise);
            odomZ   = delta.z * odomScale + odomDriftAccum.y + Random.Range(-odomNoise, odomNoise);
            heading = heading + odomHeadingDriftAccum + Random.Range(-odomNoise, odomNoise) * 30f;
        }

        // heading не проходил через Clamp (пробел) - теперь защищён так же, как одометрия.
        // Оборачиваем -180..180 перед делением, а не полагаемся на голое /180f.
        float headingWrapped = Mathf.DeltaAngle(0f, heading); // приводит к диапазону -180..180
        sensor.AddObservation(Mathf.Clamp(odomX / 3f, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(odomZ / 3f, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(headingWrapped / 180f, -1f, 1f));
        // 14. Текущая скорость движения робота, нормализована к максимуму ~0.5 м/с
        sensor.AddObservation(Mathf.Clamp01((track != null ? track.CurrentSpeed : 0f) / 0.5f));
        // 15. Время с последней детекции мяча, с учётом dropout (нормализуем к 5 сек)
        sensor.AddObservation(Mathf.Clamp01(timeSinceBallSeen / 5f));
    }

    // ---------- Шаг 3.2: приём действий ----------
    public override void OnActionReceived(ActionBuffers actions)
    {
        // Мяч в клешне: робот замирает и должен стабильно удержать его ~1 секунду.
        // Это ближе к реальности, чем мгновенный успех: на настоящем роботе мяч
        // нередко выскальзывает сразу после захвата.
        // Момент фактического захвата: was not holding -> now holding. Считаем реальные взятия мяча.
        if (gripper != null && gripper.IsHolding && !prevHolding) grabCount++;
        prevHolding = gripper != null && gripper.IsHolding;

        if (gripper != null && gripper.IsHolding)
        {
            if (track != null) track.SetDriveCommand(0f, 0f);

            // Держим мяч — робот стоит. Ноль шлём ЯВНО: иначе PublishCommand не вызывается,
            // watchdog через 0.5 с решит, что агент умер, и завалит лог предупреждениями.
            if (!isTraining && rosBridge != null) rosBridge.PublishCommand(0f, 0f);

            holdTicks++;
            AddReward(holdTickReward);
            LogDiagnosticStep(0f, 0f, camServoAngle);
            if (holdTicks >= holdTicksRequired)
            {
                AddReward(grabReward);
                EndAttempt(RobotStats.Outcome.Success);
                return;
            }
            // Пока держим мяч, shaping не начисляется (мы вышли раньше ComputeRewards),
            // но потенциал надо вести дальше: иначе после ReleaseBall() разница
            // gamma*Ф(s') - Ф(s) посчитается от протухшего Ф и даст ложный скачок.
            prevPotential = CurrentPotential();
            return;
        }

        // ---------- Шаг 3 DR: пропускаем команды через очередь задержки ----------
        // Свежая команда нейросети кладётся в хвост FIFO, а исполняется команда,
        // отданная 8-13 тиков (160-260 мс) назад — как через реальный Wi-Fi/ROS.
        float gasCmd, steerCmd, camCmd;
        if (currentActionLatency > 0)
        {
            actionBuffer.Enqueue(new float[]
            {
                SafeAction(actions.ContinuousActions[0]),
                SafeAction(actions.ContinuousActions[1]),
                SafeAction(actions.ContinuousActions[2])
            });
            float[] delayed = actionBuffer.Dequeue();
            gasCmd   = delayed[0];
            steerCmd = delayed[1];
            camCmd   = delayed[2];
        }
        else
        {
            // Без задержки — ручной режим или инференс на реальном роботе
            gasCmd   = SafeAction(actions.ContinuousActions[0]);
            steerCmd = SafeAction(actions.ContinuousActions[1]);
            camCmd   = SafeAction(actions.ContinuousActions[2]);
        }
        int gripCmd = actions.DiscreteActions[0]; // 0 стоять, 1 закрыть, 2 открыть

        if (track != null)
            track.SetDriveCommand(gasCmd, steerCmd);

        // Поворачиваем "глаз" камеры плавно, в пределах ±camServoMaxAngle
        // ОТНОСИТЕЛЬНО заводского поворота (камера изначально смотрит вперёд и чуть вниз)
        camServoAngle = Mathf.Clamp(
            camServoAngle + camCmd * camServoSpeed * Time.fixedDeltaTime,
            -camServoMaxAngle, camServoMaxAngle);
        if (eye != null)
            eye.localRotation = eyeStartLocalRot * Quaternion.Euler(0f, camServoAngle, 0f);

        if (gripper != null)
        {
            if (gripCmd == 1 && !gripper.IsHolding)
            {
                gripper.Grab();
                // Промах: скомандовал "закрыть", а мяч не схватился (его нет в клешне).
                // Штраф гасит бессмысленное "щёлканье" клешнёй.
                if (grabMissPenalty > 0f && !gripper.IsHolding)
                {
                    AddReward(-grabMissPenalty);
                    rGrab += -grabMissPenalty;
                }
            }
            if (gripCmd == 2 &&  gripper.IsHolding) gripper.ReleaseBall();
        }

        // ---------- Мост в ROS (P3, шаг 8) ----------
        // Только на инференсе: на обучении ROSBridge отключает сам себя, но лишний
        // вызов всё равно не делаем. Шлём ТЕ ЖЕ команды, что ушли в TrackController,
        // т.е. уже прошедшие через очередь задержки — реальному роботу дублировать
        // симулированный лаг не нужно, но зато sim и real исполняют одно и то же.
        if (!isTraining && rosBridge != null)
        {
            rosBridge.PublishCommand(gasCmd, steerCmd);
            rosBridge.PublishCameraCmd(camServoAngle); // градусы, а не сырое действие
            if (gripCmd != 0) rosBridge.PublishGripperCmd(gripCmd);
        }

        ComputeRewards(gasCmd, steerCmd, camCmd);
        LogDiagnosticStep(gasCmd, steerCmd, camServoAngle);
    }

    // ---------- Практика 6: диагностический лог (если DiagnosticLogger подключён) ----------
    // Чистый наблюдатель: только читает уже посчитанные значения, ничего не меняет
    // в награде/наблюдениях/действиях. Безопасен для обучения даже если оставлен вкл.
    private void LogDiagnosticStep(float gas, float steering, float camYawDeg)
    {
        if (diagLogger == null) return;

        bool  ballSeen  = cam != null && cam.BallVisible;
        float ballAngle = ballSeen ? cam.HorizontalOffset   : 0f; // нормализовано -1..1 (см. заголовок CSV), не градусы
        float ballDist  = ballSeen ? cam.NormalizedDistance : 1f;

        // Эвристика isRetrying (ТОЛЬКО для лога, не влияет на поведение/награду):
        // мяч был виден и близко -> пропал из виду -> считаем уместным манёвр отъезда
        // на retryDetectTicks тиков (P6, шаг 3: "должна кратковременно включаться фаза Retry").
        bool closeAndVisible = ballSeen && ballDist < retryDetectDist;
        if (wasCloseAndVisible && !ballSeen) retryFlagTicksLeft = retryDetectTicks;
        else if (retryFlagTicksLeft > 0)     retryFlagTicksLeft--;
        wasCloseAndVisible = closeAndVisible;
        bool isRetrying = retryFlagTicksLeft > 0;

        // ИСТИННОЕ смещение от старта эпизода (без шума одометрии из наблюдений) —
        // лог нужен для честного анализа траектории, а не для того, что "видит" сеть.
        Vector3 delta = transform.position - episodeStartPosition;

        diagLogger.LogStep(
            StepCount,
            ballSeen, ballAngle, ballDist,
            sensors != null ? sensors.Ultrasonic          : 1f,
            sensors != null ? Mathf.RoundToInt(sensors.LeftIR)    : 0,
            sensors != null ? Mathf.RoundToInt(sensors.RightIR)   : 0,
            sensors != null ? Mathf.RoundToInt(sensors.GripperIR) : 0,
            camYawDeg, gas, steering,
            gripper != null && gripper.IsHolding, holdTicks, isRetrying,
            delta.x, delta.z,
            transform.eulerAngles.y / 360f, // формула по P6 для лога (отличается от heading в наблюдениях)
            track != null ? track.CurrentSpeed : 0f // rb.linearVelocity ~0 (робот двигается через MovePosition) - реальная скорость из TrackController, как и в наблюдении №14
        );
    }

    // Первообразная веса сближения из методички (P3, шаг 4.1: «Сближение вблизи мяча
    // рекомендуется поощрять сильнее, чем вдали»). Вес w(d) = 1.5 - clamp01(d/2),
    // и мы берём Ф_dist(d) = -∫w(d)dd, так что Ф'(d) = -w(d).
    // Смысл: подъезд на 1 м вплотную к мячу даёт втрое больше, чем в 2 м от него —
    // ровно как требует методичка. Но, в отличие от старого delta*w(d_new), сумма
    // по замкнутому маршруту строго равна нулю, и «подъехал-отъехал» больше не доится.
    // Проверка на NaN/Infinity сразу по всем трём осям (защита от "взрыва" физики).
    private static bool IsFiniteVector(Vector3 v)
        => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    // Санитизация действия от сети. ВАЖНО: Mathf.Clamp НЕ фильтрует NaN — сравнения
    // с NaN всегда ложны, поэтому Mathf.Clamp(NaN, -1, 1) возвращает NaN как есть.
    // Если сеть хоть раз отдаст NaN-действие (редкий численный сбой, напр. сразу
    // после resume с изменённой семантикой наблюдения), camServoAngle накопит его
    // НАВСЕГДА (camServoAngle += camCmd*... каждый тик), что рано или поздно
    // роняет обучение с "observations had NaN values". Ловим на входе: плохое
    // действие -> считаем нулевым за этот тик, не даём ему просочиться дальше.
    private static float SafeAction(float v)
        => float.IsFinite(v) ? Mathf.Clamp(v, -1f, 1f) : 0f;

    // Нормальный шум N(0,1) через Box-Muller (в UnityEngine.Random его нет).
    // ВАЖНО: Random.value в Unity включает 1.0 (диапазон [0,1] inclusive с ОБЕИХ
    // сторон) - "1f - Random.value" мог реально стать РОВНО 0, и тогда
    // Log(0) = -Infinity, дальше Sqrt(+Infinity) = +Infinity, а если Cos(2*pi*u2)
    // попадёт РОВНО в 0 - Infinity*0 = NaN. Это тикает в odomDriftAccum/
    // odomHeadingDriftAccum КАЖДЫЙ шаг на КАЖДОЙ из параллельных арен, поэтому
    // редкое per-tick событие на масштабе многих миллионов шагов рано или поздно
    // случается - именно отсюда крахи "observations had NaN values", не имеющие
    // отношения к обс.7 или camServoAngle. Явный epsilon вместо надежды, что
    // Random.value никогда не даст ровно 1.0.
    private static float RandGaussian()
    {
        float u1 = Mathf.Max(1f - Random.value, 1e-7f);
        float u2 = Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }

    private static float DistancePotential(float d)
    {
        if (d <= 2f) return -(1.5f * d - 0.25f * d * d);
        return -2f - 0.5f * (d - 2f);   // дальше 2 м вес постоянный (0.5), сшито непрерывно
    }

    // ---------- Потенциал Ф(s) для shaping'а ----------
    // «Насколько хорошо роботу прямо сейчас»: ближе к мячу + мордой к мячу.
    // Считается по ЧЕСТНОЙ геометрии, а не по зашумлённому наблюдению, иначе агент
    // начнёт фармить шум Domain Randomization. Это законно: награда существует только
    // во время обучения, политика по-прежнему видит одни лишь 15 наблюдений.
    private float CurrentPotential()
    {
        if (ball == null) return 0f;

        Vector3 toBall = ball.position - transform.position;
        toBall.y = 0f;

        float phi = approachRewardScale * DistancePotential(toBall.magnitude);

        // Центрирование (P3, шаг 4.3) считаем по ИСТИННОМУ пеленгу, а НЕ по cam.BallVisible.
        // Это принципиально. Привязка к видимости давала обрыв потенциала на границе кадра:
        // мяч уходит под бампер перед захватом -> Ф проваливается -> у робота появляется
        // повод пятиться назад, лишь бы вернуть мяч в кадр. А по теории P3 слепая зона
        // перед захватом штатна, и проезжать её должна LSTM-память. Пеленг непрерывен
        // везде, обрыва нет, и вход в слепую зону больше ничего не стоит.
        float bearing = Vector3.SignedAngle(transform.right, toBall, Vector3.up); // перёд робота = +X
        phi += centeringRewardScale * (1f - Mathf.Abs(bearing) / 180f);

        return phi;
    }

    // ---------- Шаг 4: награды ----------
    private void ComputeRewards(float gasCmd, float steerCmd, float camCmd)
    {
        // 1. Potential-based shaping: r = gamma*Ф(s') - Ф(s).
        //    Теорема Ng et al. (1999): такая добавка НЕ меняет оптимальную политику
        //    и не создаёт «вечных двигателей» — сумма по любому циклу равна ~0.
        //    Раньше здесь было два фармящихся источника:
        //      * центрирование платило 0.01/тик, пока мяч виден -> выгодно было
        //        стоять в кольце видимости все 20 с (1000 тиков * 0.01 = +10),
        //        что БОЛЬШЕ, чем захват (5.0) с удержанием (1.0);
        //      * вес сближения (0.5 + closeWeight) считался по НОВОЙ дистанции и падал
        //        с расстоянием, поэтому цикл «подъехал-отъехал» давал плюс на ровном месте.
        //    Оба исчезли: платит только фактический прогресс к мячу.
        float phi = CurrentPotential();
        float shapingReward = shapingGamma * phi - prevPotential;
        AddReward(shapingReward);
        rApproach += shapingReward;
        prevPotential = phi;

        // 2. Штраф за резкость управления (Action Rate Penalty). Единый штраф на газ, руль
        //    И камеру — этого достаточно, отдельные штрафы за камеру/реверс газа были
        //    избыточны (дублировали этот же сигнал) и убраны для простоты.
        float actionRate = Mathf.Abs(gasCmd - prevGas) + Mathf.Abs(steerCmd - prevSteer)
                         + Mathf.Abs(camCmd - prevCam);
        float actionPen = -actionRate * actionPenaltyScale;
        AddReward(actionPen);
        rActionPen += actionPen;

        prevGas   = gasCmd;
        prevSteer = steerCmd;
        prevCam   = camCmd;

        // 2a. Ургентность: штраф за каждый шаг, чтобы робот не тянул время.
        AddReward(-timePenalty);
        rTimePen += -timePenalty;

        // 2b. Штрафы за направление движения ("движение в ту или иную сторону").
        //     spin  — за |поворот| (по умолчанию 0: при узком FOV робот обязан вертеться);
        //     forward — за газ вперёд (обычно 0, вперёд ехать полезно);
        //     reverse — за газ назад (камера и клешня спереди, реверс цель не приближает).
        if (spinPenalty > 0f)
        {
            float p = -Mathf.Abs(steerCmd) * spinPenalty;
            AddReward(p); rDirPen += p;
        }
        if (forwardPenalty > 0f && gasCmd > 0f)
        {
            float p = -gasCmd * forwardPenalty;
            AddReward(p); rDirPen += p;
        }
        if (reversePenalty > 0f && gasCmd < 0f)
        {
            float p = gasCmd * reversePenalty; // gasCmd<0 -> награда отрицательная
            AddReward(p); rDirPen += p;
        }

        // 3. Штраф за БЛИЗОСТЬ к преграде по боковым ИК (ещё не касание, а сближение).
        if (sensors != null && (sensors.LeftIR > 0.5f || sensors.RightIR > 0.5f))
        {
            AddReward(-obstaclePenalty);
            rObstProx += -obstaclePenalty;
        }

        // 3b. Помощь захвату: пока мяч уже в луче клешни (gripperIR=1), но ещё не схвачен —
        //     платим за пребывание в позе захвата. Это усиливает слабый сигнал «последнего
        //     метра» и учит держать мяч в клешне, пока не сработает Close.
        if (gripperIrReward != 0f && sensors != null && sensors.GripperIR > 0.5f
            && !(gripper != null && gripper.IsHolding))
        {
            AddReward(gripperIrReward);
            rGrab += gripperIrReward;
        }

        // 3c. РАЗОВЫЙ бонус за первое обнаружение мяча в эпизоде. Ключевой сигнал для
        //     обучения ПОИСКУ: без него «найти скрытый мяч» — слишком редкое событие,
        //     чтобы политика за него зацепилась. Даётся один раз (farm невозможен).
        if (searchFindReward > 0f && !everSeenBall && cam != null && cam.BallVisible)
        {
            AddReward(searchFindReward);
            rApproach += searchFindReward;
            everSeenBall = true;
        }

        // 3e. Прямой стимул для СЕРВОПРИВОДА КАМЕРЫ активно следить за мячом. Без этого
        //     камера получала стимул только КОСВЕННО (лучше видно -> легче доехать), а такого
        //     слабого сигнала мало для отдельного действия (action 2). Малая величина и
        //     строгое условие ballVisibleReward < time_penalty не дают эксплойт "зависнуть,
        //     лишь бы мяч был в кадре" — тот же принцип защиты, что и у gripper_ir_reward.
        if (ballVisibleReward > 0f && cam != null && cam.BallVisible)
        {
            AddReward(ballVisibleReward);
            rApproach += ballVisibleReward;
        }

        // 3a. Штрафы за ФАКТИЧЕСКОЕ столкновение (флаги взведены в OnCollision*).
        //     Раздельно: стена и преграда/коробка. Контакты с полом отфильтрованы по нормали.
        if (wallTouch)
        {
            AddReward(-wallCollisionPenalty);
            rWallCol += -wallCollisionPenalty;
        }
        if (obstacleTouch)
        {
            AddReward(-obstacleCollisionPenalty);
            rObstCol += -obstacleCollisionPenalty;
        }
        wallTouch = obstacleTouch = false;

        // 4. Терминальные условия
        // Успех (захват + удержание) обрабатывается в начале OnActionReceived,
        // здесь остались только неудачи: падение и таймаут.
        if (transform.position.y < fallY)
        {
            AddReward(fallPenalty);
            EndAttempt(RobotStats.Outcome.Fall);
            return;
        }

        // Лимит времени: если за отведённые секунды робот не схватил мяч и не упал —
        // считаем попытку неудачной и начинаем эпизод заново
        episodeTimer += Time.fixedDeltaTime;
        if (episodeTimer >= episodeTimeLimit)
        {
            AddReward(timeoutPenalty);
            EndAttempt(RobotStats.Outcome.Timeout);
        }
    }

    // ---------- Столкновения ----------
    // Робот двигается через MovePosition на НЕкинематическом Rigidbody, поэтому
    // callbacks столкновений исправно приходят. Контакт засчитываем только если:
    //   1) нормаль почти горизонтальна (вертикальная = пол/потолок, игнор — пол в сцене
    //      лежит на слое Obstacle, и без этого робот штрафовался бы просто за езду);
    //   2) слой объекта попал в маску стен или преград.
    // Enter считаем отдельным "врезанием" (для счётчика), Stay — только держит флаг штрафа.
    void OnCollisionEnter(Collision c) => ClassifyContact(c, true);
    void OnCollisionStay(Collision c)  => ClassifyContact(c, false);

    private void ClassifyContact(Collision c, bool isNewContact)
    {
        int layer = c.gameObject.layer;
        bool inWall = (wallCollisionMask.value     & (1 << layer)) != 0;
        bool inObst = (obstacleCollisionMask.value & (1 << layer)) != 0;
        if (!inWall && !inObst) return;

        bool horizontal = false;
        for (int i = 0; i < c.contactCount; i++)
        {
            if (Mathf.Abs(c.GetContact(i).normal.y) < collisionFloorNormalY)
            {
                horizontal = true;
                break;
            }
        }
        if (!horizontal) return; // только пол/потолок — не считаем столкновением

        if (inWall)      { wallTouch     = true; if (isNewContact) wallHitCount++;     }
        else if (inObst) { obstacleTouch = true; if (isNewContact) obstacleHitCount++; }
    }

    // ---------- Завершение попытки со сбором статистики ----------
    // Единая точка выхода из эпизода: сначала отдаём метрики в RobotStats,
    // затем зовём EndEpisode(). Три исхода: успех, падение, таймаут.
    private void EndAttempt(RobotStats.Outcome outcome)
    {
        if (stats != null)
        {
            var s = new RobotStats.EpisodeResult
            {
                outcome     = outcome,
                durationSec = episodeTimer,
                grabs       = grabCount,
                wallHits    = wallHitCount,
                obstacleHits = obstacleHitCount,
                totalReward = GetCumulativeReward(),
                rApproach   = rApproach,
                rActionPen  = rActionPen,
                rTimePen    = rTimePen,
                rObstProx   = rObstProx,
                rWallCol    = rWallCol,
                rObstCol    = rObstCol,
                rDirPen     = rDirPen,
                rGrab       = rGrab
            };
            stats.RecordEpisode(s);
        }
        EndEpisode();
    }

    // ---------- Ручной режим для проверки без обученной модели ----------
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var cont = actionsOut.ContinuousActions;
        var disc = actionsOut.DiscreteActions;

        float g = 0f, s = 0f, c = 0f;
        int   grip = 0;

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)   g += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) g -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) s += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  s -= 1f;
            if (kb.qKey.wasPressedThisFrame) grip = 1; // Q — сжать клешню
            if (kb.eKey.wasPressedThisFrame) grip = 2; // E — разжать клешню
            // Z/C крутят сервопривод камеры вручную (для ручных проверок в Play).
            if (kb.cKey.isPressed) c += 1f;
            if (kb.zKey.isPressed) c -= 1f;
        }
#endif
        cont[0] = g;
        cont[1] = s;
        cont[2] = c;
        disc[0] = grip;
    }
}