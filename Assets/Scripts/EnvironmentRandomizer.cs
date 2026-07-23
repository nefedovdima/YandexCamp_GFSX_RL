using UnityEngine;

// Рандомизация окружения (расширение Практики 5 под нашу арену).
// Layout (коробки) и физические свойства (свет/трение) можно включать независимо.
public class EnvironmentRandomizer : MonoBehaviour
{
    [Header("Арена (прямоугольник по X/Z)")]
    // Если arenaOrigin ЗАДАН — центр арены = arenaOrigin.position + arenaCenter (локально).
    // Тогда несколько копий арены (prefab) на разных позициях рандомизируются каждая
    // вокруг СВОЕГО центра. Если arenaOrigin ПУСТ — arenaCenter трактуется как мировые
    // координаты (прежнее поведение единственной арены, ничего не меняется).
    [SerializeField] private Transform arenaOrigin;
    [SerializeField] private Vector2 arenaCenter = Vector2.zero;
    [SerializeField] private Vector2 arenaSize   = new Vector2(3f, 3f);
    [SerializeField] private float   wallMargin  = 0.25f; // отступ от стен при расстановке

    [Header("Коробки-препятствия")]
    [SerializeField] private Transform[] boxes;
    [SerializeField] private Vector2Int activeBoxCount = new Vector2Int(4, 15); // сколько коробок включать за эпизод (мин/макс)
    [SerializeField] private float boxSpacing       = 0.6f; // мин. расстояние между коробками
    [SerializeField] private float robotClearRadius = 0.7f; // вокруг спавна робота коробки не ставим
    [SerializeField] private Transform robot;

    [Header("Освещение")]
    [SerializeField] private Light[] lights;
    [SerializeField] private Vector2 intensityRange = new Vector2(0.5f, 1.5f); // множитель к заводской яркости
    [SerializeField] private float   colorJitter    = 0.08f;                   // насыщенность случайного оттенка (0 = всегда белый)
    [SerializeField] private Vector2 sunPitchRange  = new Vector2(30f, 75f);   // наклон Directional-света над горизонтом

    [Header("Трение пола")]
    [SerializeField] private Collider floorCollider;
    [SerializeField] private Vector2  frictionRange = new Vector2(0.15f, 1.0f); // от скользкого линолеума до ковра

    [Header("Спавн мяча")]
    [SerializeField] private LayerMask obstacleMask;              // слой коробок/стен
    [SerializeField] private float ballRobotMinDistance = 0.5f;   // мяч не появляется вплотную к роботу
    [SerializeField] private float maxSpawnRadius       = 0f;     // 0 = вся арена; >0 = мяч не дальше R от робота (для curriculum)
    private float ballWallMargin = -1f;                           // <0 = legacy wallMargin + ballRadius

    // Максимальный радиус спавна мяча вокруг робота. RobotBrain задаёт его из
    // config.yaml (ball_spawn_radius): на старте curriculum мяч рядом, потом дальше.
    public float MaxSpawnRadius { get => maxSpawnRadius; set => maxSpawnRadius = value; }

    // Минимальное расстояние ЦЕНТРА мяча до внутренней границы арены.
    // Отрицательное значение сохраняет прежний отступ wallMargin + ballRadius.
    public float BallWallMargin { get => ballWallMargin; set => ballWallMargin = value; }

    // Сколько коробок-преград включать за эпизод (min, max). RobotBrain задаёт из
    // config.yaml (box_count_min/max): поставь (0,0) — преград не будет вовсе.
    public Vector2Int ActiveBoxCount { get => activeBoxCount; set => activeBoxCount = value; }

    // Заводское состояние — для восстановления на инференсе
    private Vector3[]    boxStartPos;
    private Quaternion[] boxStartRot;
    private bool[]       boxStartActive;
    private int[]        boxOrder; // перемешиваемый список индексов коробок
    private float[]      lightStartIntensity;
    private Color[]      lightStartColor;
    private Quaternion[] lightStartRot;
    private PhysicsMaterial floorMat;
    private float        floorStartFriction;
    private bool         ballSpawnWarningIssued;

    void Awake()
    {
        if (boxes != null)
        {
            boxStartPos    = new Vector3[boxes.Length];
            boxStartRot    = new Quaternion[boxes.Length];
            boxStartActive = new bool[boxes.Length];
            boxOrder       = new int[boxes.Length];
            for (int i = 0; i < boxes.Length; i++)
            {
                boxOrder[i] = i;
                if (boxes[i] == null) continue;
                boxStartPos[i]    = boxes[i].position;
                boxStartRot[i]    = boxes[i].rotation;
                boxStartActive[i] = boxes[i].gameObject.activeSelf;
            }
        }

        if (lights != null)
        {
            lightStartIntensity = new float[lights.Length];
            lightStartColor     = new Color[lights.Length];
            lightStartRot       = new Quaternion[lights.Length];
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] == null) continue;
                lightStartIntensity[i] = lights[i].intensity;
                lightStartColor[i]     = lights[i].color;
                lightStartRot[i]       = lights[i].transform.rotation;
            }
        }

        if (floorCollider != null)
        {
            // .material даёт личный экземпляр материала (не портит общий ассет)
            floorMat = floorCollider.material;
            if (floorMat == null)
            {
                floorMat = new PhysicsMaterial("FloorRuntime");
                floorCollider.material = floorMat;
            }
            floorStartFriction = floorMat.dynamicFriction;
        }
    }

    // ---------- Главные входные точки ----------

    public void Randomize()
    {
        Randomize(true, true);
    }

    public void Randomize(bool randomizeLayout, bool randomizePhysical)
    {
        ballSpawnWarningIssued = false;

        if (randomizeLayout) RandomizeBoxes();
        else                 RestoreBoxes();

        if (randomizePhysical)
        {
            RandomizeLights();
            RandomizeFriction();
        }
        else
        {
            RestorePhysicalDefaults();
        }
    }

    public void RestoreDefaults()
    {
        ballSpawnWarningIssued = false;
        RestoreBoxes();
        RestorePhysicalDefaults();
    }

    private void RestoreBoxes()
    {
        if (boxes != null && boxStartPos != null)
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i] == null) continue;
                boxes[i].gameObject.SetActive(boxStartActive[i]);
                boxes[i].SetPositionAndRotation(boxStartPos[i], boxStartRot[i]);
            }
    }

    private void RestorePhysicalDefaults()
    {
        if (lights != null && lightStartIntensity != null)
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] == null) continue;
                lights[i].intensity          = lightStartIntensity[i];
                lights[i].color              = lightStartColor[i];
                lights[i].transform.rotation = lightStartRot[i];
            }

        if (floorMat != null)
        {
            floorMat.dynamicFriction = floorStartFriction;
            floorMat.staticFriction  = floorStartFriction;
        }
    }

    // Случайная точка для мяча: внутри арены, не внутри коробки, не вплотную к роботу.
    // reject (опционально): предикат "эту точку НЕ брать". RobotBrain передаёт сюда
    // проверку видимости с камеры, чтобы гарантировать спавн "за стеной"/вне кадра.
    // Возвращает null, если за N попыток место не нашлось (вызывающий откатится на дефолт).
    public Vector3? SampleBallSpawn(float ballRadius, float ballY,
                                    System.Func<Vector3, bool> reject = null)
    {
        float checkRadius = ballRadius * 1.5f;
        float centerWallMargin = EffectiveBallWallMargin(ballRadius);
        // Для скрытого спавна условие жёстче (нужна точка за преградой/вне кадра),
        // поэтому даём больше попыток, когда предикат задан.
        int attempts = reject != null ? 60 : 30;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Vector3 pos = RandomPointInArena(centerWallMargin);
            pos.y = ballY;

            if (!IsBallSpawnValid(pos, checkRadius, reject)) continue;

            return pos;
        }

        // Скрытый spawn имеет внешний reject-фильтр. Сначала даём RobotBrain повторить
        // поиск без него; fallback нужен только после обычного исчерпания попыток.
        if (reject != null) return null;

        Vector3 fallback = FindBoundedBallFallback(ballRadius, ballY, checkRadius);
        WarnBallSpawnFallback(centerWallMargin, fallback);
        return fallback;
    }

    private bool IsBallSpawnValid(Vector3 pos, float checkRadius,
                                  System.Func<Vector3, bool> reject)
    {
        if (robot != null)
        {
            Vector3 flat = pos - robot.position;
            flat.y = 0f;
            if (flat.magnitude < ballRobotMinDistance) return false;
            // Curriculum: на ранних уроках держим мяч ближе к роботу.
            if (maxSpawnRadius > 0f && flat.magnitude > maxSpawnRadius) return false;
        }

        // Пол тоже лежит на слое Obstacle, поэтому сферу поднимаем над ним.
        Vector3 checkPos = pos;
        checkPos.y = Mathf.Max(pos.y, checkRadius + 0.02f);
        if (Physics.CheckSphere(checkPos, checkRadius, obstacleMask,
                                QueryTriggerInteraction.Ignore)) return false;

        return reject == null || !reject(pos);
    }

    private Vector3 FindBoundedBallFallback(float ballRadius, float ballY,
                                            float checkRadius)
    {
        float margin = EffectiveBallWallMargin(ballRadius);
        float halfX = Mathf.Max(0f, arenaSize.x * 0.5f - margin);
        float halfZ = Mathf.Max(0f, arenaSize.y * 0.5f - margin);
        float cx = arenaCenter.x + (arenaOrigin != null ? arenaOrigin.position.x : 0f);
        float cz = arenaCenter.y + (arenaOrigin != null ? arenaOrigin.position.z : 0f);

        // Детерминированная ограниченная сетка от центра наружу. Обычно она находит
        // свободную точку; даже последний fallback остаётся внутри wall margin.
        const int rings = 3;
        for (int ring = 0; ring <= rings; ring++)
        {
            for (int x = -ring; x <= ring; x++)
            {
                for (int z = -ring; z <= ring; z++)
                {
                    if (ring > 0 && Mathf.Abs(x) != ring && Mathf.Abs(z) != ring) continue;

                    float nx = rings > 0 ? x / (float)rings : 0f;
                    float nz = rings > 0 ? z / (float)rings : 0f;
                    Vector3 candidate = new Vector3(
                        cx + nx * halfX * 0.9f,
                        ballY,
                        cz + nz * halfZ * 0.9f);

                    if (IsBallSpawnValid(candidate, checkRadius, null))
                        return candidate;
                }
            }
        }

        return new Vector3(cx, ballY, cz);
    }

    private float EffectiveBallWallMargin(float ballRadius)
    {
        if (ballWallMargin < 0f)
            return wallMargin + ballRadius;

        // Центр не должен ставить collider за стену даже при ошибочном слишком
        // маленьком параметре.
        return Mathf.Max(ballWallMargin, ballRadius);
    }

    private void WarnBallSpawnFallback(float centerWallMargin, Vector3 fallback)
    {
        if (ballSpawnWarningIssued) return;
        ballSpawnWarningIssued = true;
        Debug.LogWarning(
            $"{name}: no random ball spawn was found; using bounded fallback " +
            $"{fallback} with center wall margin {centerWallMargin:F3} m.",
            this);
    }

    // Случайная точка спавна РОБОТА: внутри арены (отступ wallMargin — не в стене),
    // не внутри коробки/преграды. Проверяется по ТЕКУЩИМ (ещё не переставленным на этот
    // эпизод) позициям коробок — вызывать ДО RandomizeBoxes(), тогда сам RandomizeBoxes()
    // расставит коробки уже вокруг новой точки робота через существующий robotClearRadius.
    // Возвращает null, если за 30 попыток место не нашлось (вызывающий откатится на дефолт).
    public Vector3? SampleRobotSpawn(float robotRadius, float robotY)
    {
        float checkRadius = robotRadius * 1.2f;

        for (int attempt = 0; attempt < 30; attempt++)
        {
            Vector3 pos = RandomPointInArena(wallMargin + robotRadius);
            pos.y = robotY;

            // Не внутри коробки (см. пояснение к ballY-подъёму в SampleBallSpawn —
            // пол на слое Obstacle, поэтому сферу проверки поднимаем над ним).
            Vector3 checkPos = pos;
            checkPos.y = Mathf.Max(robotY, checkRadius + 0.02f);
            if (Physics.CheckSphere(checkPos, checkRadius, obstacleMask,
                                    QueryTriggerInteraction.Ignore)) continue;

            return pos;
        }
        return null;
    }

    // ---------- Внутренности ----------

    private Vector3 RandomPointInArena(float margin)
    {
        float halfX = Mathf.Max(0f, arenaSize.x * 0.5f - margin);
        float halfZ = Mathf.Max(0f, arenaSize.y * 0.5f - margin);
        // Мировой центр арены: с origin — относительно него (prefab-копии), без — абсолютный.
        float cx = arenaCenter.x + (arenaOrigin != null ? arenaOrigin.position.x : 0f);
        float cz = arenaCenter.y + (arenaOrigin != null ? arenaOrigin.position.z : 0f);
        return new Vector3(
            cx + Random.Range(-halfX, halfX),
            0f,
            cz + Random.Range(-halfZ, halfZ));
    }

    private void RandomizeBoxes()
    {
        if (boxes == null || boxes.Length == 0) return;

        // Сколько коробок будет стоять в этом эпизоде: от min до max,
        // но не больше, чем реально есть в массиве
        int want = Mathf.Clamp(
            Random.Range(activeBoxCount.x, activeBoxCount.y + 1),
            0, boxes.Length);

        // Перемешиваем индексы (Фишер-Йетс): каждый эпизод активен другой набор коробок
        for (int i = boxOrder.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (boxOrder[i], boxOrder[j]) = (boxOrder[j], boxOrder[i]);
        }

        int placedCount = 0;
        for (int k = 0; k < boxOrder.Length; k++)
        {
            int i = boxOrder[k];
            if (boxes[i] == null) continue;

            // Лишние коробки на этот эпизод просто выключаем
            if (placedCount >= want)
            {
                boxes[i].gameObject.SetActive(false);
                continue;
            }

            boxes[i].gameObject.SetActive(true);

            bool placed = false;
            for (int attempt = 0; attempt < 30 && !placed; attempt++)
            {
                Vector3 pos = RandomPointInArena(wallMargin);
                pos.y = boxStartPos[i].y; // высоту не трогаем — коробка стоит на полу

                // Не на спавне робота
                if (robot != null)
                {
                    Vector3 flat = pos - robot.position;
                    flat.y = 0f;
                    if (flat.magnitude < robotClearRadius) continue;
                }

                // Не вплотную к уже расставленным в этом эпизоде коробкам
                bool tooClose = false;
                for (int m = 0; m < k; m++)
                {
                    int j = boxOrder[m];
                    if (boxes[j] == null || !boxes[j].gameObject.activeSelf) continue;
                    Vector3 d = pos - boxes[j].position;
                    d.y = 0f;
                    if (d.magnitude < boxSpacing) { tooClose = true; break; }
                }
                if (tooClose) continue;

                boxes[i].SetPositionAndRotation(
                    pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
                placed = true;
            }

            // Если место не нашлось — заводская позиция со случайным поворотом
            if (!placed)
                boxes[i].SetPositionAndRotation(
                    boxStartPos[i], Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));

            placedCount++;
        }
    }

    private void RandomizeLights()
    {
        if (lights == null) return;

        for (int i = 0; i < lights.Length; i++)
        {
            var l = lights[i];
            if (l == null) continue;

            // Яркость и лёгкий цветовой оттенок: блики и цветовая температура
            // в реальном зале не совпадут с симулятором, сеть не должна на них опираться
            l.intensity = lightStartIntensity[i] * Random.Range(intensityRange.x, intensityRange.y);
            l.color     = lightStartColor[i] * Random.ColorHSV(0f, 1f, 0f, colorJitter, 0.9f, 1f);

            // Направленный свет ещё и приходит с другой стороны — тени ложатся иначе
            if (l.type == LightType.Directional)
                l.transform.rotation = Quaternion.Euler(
                    Random.Range(sunPitchRange.x, sunPitchRange.y),
                    Random.Range(0f, 360f),
                    0f);
        }
    }

    private void RandomizeFriction()
    {
        if (floorMat == null) return;

        // Ковёр или скользкий линолеум. Прямо на робота почти не влияет (он движется
        // через MovePosition), но меняет поведение мяча при толчках и столкновениях.
        // Разброс сцепления самого робота уже покрыт рандомизацией moveSpeed/maxPwmStep.
        float f = Random.Range(frictionRange.x, frictionRange.y);
        floorMat.dynamicFriction = f;
        floorMat.staticFriction  = f;
    }
}
