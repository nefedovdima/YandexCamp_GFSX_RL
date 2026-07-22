using UnityEngine;

// Имитация бортовой камеры + YOLO (по методичке, через WorldToViewportPoint).
// Вешается на камеру-ребёнка робота, которая смотрит вдоль его движения.
// Поле зрения (пирамида) задаётся настройками самого компонента Camera.
[RequireComponent(typeof(Camera))]
public class SimulatedYoloCamera : MonoBehaviour
{
    [Header("Цель")]
    [SerializeField] private Transform targetBall;   // перетащите сюда Target_ball

    [Header("Оптика (P3: hFOV = 40 градусов)")]
    // hFOV = 40° — прямое требование методички (P3, шаг 2, «Проверка видимости»).
    // Вертикаль выведена из сенсора 36x24 мм и фокуса 49.45 мм, прописанных в сцене:
    // 2*atan(12/49.45) = 27.3°. Те же параметры дают 2*atan(18/49.45) = ровно 40° по
    // горизонтали, т.е. объектив был настроен верно — но при m_projectionMatrixMode: 1
    // Unity физическую камеру игнорирует и берёт поле "field of view" (там стояло 13.775).
    //
    // Задаём углы явно и применяем в Awake, потому что WorldToViewportPoint считает по
    // Camera.aspect, а тот по умолчанию берётся из РАЗМЕРА ОКНА: сектор детекции молча
    // разъезжался между редактором и headless-сборкой на сервере. Теперь frustum
    // привязан к линзе, а не к разрешению.
    [SerializeField] private float horizontalFovDeg = 40f;
    [SerializeField] private float verticalFovDeg   = 27.3f;

    [Header("Параметры распознавания")]
    [SerializeField] private float     maxRange = 2.0f;  // макс. дальность видимости, м
    [SerializeField] private LayerMask obstacleMask;     // ТОЛЬКО слой Obstacle

    [Header("Отладка")]
    [SerializeField] private bool drawRays = true;
    [SerializeField] private bool drawFov  = true;   // рисовать пирамиду обзора камеры лучами

    private Camera cam;

    // ---------- Выход для RobotBrain (наблюдения) ----------
    public bool  BallVisible        { get; private set; }         // (obs 8)
    public float HorizontalOffset   { get; private set; }         // -1 слева ... +1 справа (obs 5)
    public float NormalizedDistance { get; private set; } = 1f;   // 0 вплотную ... 1 далеко (obs 6)
    public float LastKnownOffset    { get; private set; }         // (obs 7)
    public float TimeSinceLastSeen  { get; private set; }         // (obs 15)
    public float HorizontalFovDeg   => horizontalFovDeg;           // нужен RobotBrain для пересчёта офсета в градусы
    public Transform TargetBall => targetBall;

    void Awake()
    {
        cam = GetComponent<Camera>();
        ApplyOptics();
    }

#if UNITY_EDITOR
    // Чтобы в редакторе картинка сразу соответствовала вписанным углам
    void OnValidate() { cam = GetComponent<Camera>(); ApplyOptics(); }
#endif

    // Жёстко задаёт frustum по паспортным углам линзы, независимо от размера окна.
    // Unity трактует Camera.fieldOfView как ВЕРТИКАЛЬНЫЙ угол, а горизонтальный
    // выводит из него через aspect — поэтому aspect считаем сами из двух углов.
    private void ApplyOptics()
    {
        if (cam == null) return;
        cam.usePhysicalProperties = false;
        cam.fieldOfView = verticalFovDeg;
        cam.aspect = Mathf.Tan(horizontalFovDeg * 0.5f * Mathf.Deg2Rad)
                   / Mathf.Tan(verticalFovDeg   * 0.5f * Mathf.Deg2Rad);
    }

    void FixedUpdate()
    {
        UpdateDetection();
        if (drawFov) DrawFov();
    }

    // Принудительный пересчёт детекции «прямо сейчас». Нужен RobotBrain'у в
    // OnEpisodeBegin: мяч и робот только что телепортированы, а FixedUpdate камеры
    // в этом кадре ещё не отработал, и BallVisible описывает прошлый эпизод.
    public void Refresh() => UpdateDetection();

    // Была бы видна цель, окажись она в точке worldPos ПРЯМО СЕЙЧАС (та же логика,
    // что в UpdateDetection). Нужен спавну мяча, чтобы гарантировать старт "за стеной" /
    // вне кадра. true = видна, false = скрыта.
    public bool WouldSeeBallAt(Vector3 worldPos)
    {
        if (cam == null) return false;
        Vector3 vp = cam.WorldToViewportPoint(worldPos);
        bool inFrame = vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;
        if (!inFrame) return false;
        float dist = Vector3.Distance(cam.transform.position, worldPos);
        if (dist > maxRange) return false;
        Vector3 dir = (worldPos - cam.transform.position).normalized;
        if (Physics.Raycast(cam.transform.position, dir, out _, dist - 0.01f, obstacleMask, QueryTriggerInteraction.Ignore))
            return false;
        return true;
    }

    // Рисует пирамиду обзора камеры (frustum) лучами до maxRange: зелёная, если мяч
    // виден, иначе жёлтая. Видно в Scene view всегда; в Game view — при включённых Gizmos.
    private void DrawFov()
    {
        if (cam == null) return;
        float hh = Mathf.Tan(horizontalFovDeg * 0.5f * Mathf.Deg2Rad);
        float vh = Mathf.Tan(verticalFovDeg   * 0.5f * Mathf.Deg2Rad);
        Transform t = cam.transform;
        Vector3 p = t.position;
        Vector3 f = t.forward, r = t.right, u = t.up;
        float d = maxRange;
        Vector3 c0 = p + (f + r * hh + u * vh) * d;
        Vector3 c1 = p + (f - r * hh + u * vh) * d;
        Vector3 c2 = p + (f - r * hh - u * vh) * d;
        Vector3 c3 = p + (f + r * hh - u * vh) * d;
        Color col = BallVisible ? Color.green : Color.yellow;
        Debug.DrawLine(p, c0, col); Debug.DrawLine(p, c1, col);
        Debug.DrawLine(p, c2, col); Debug.DrawLine(p, c3, col);
        Debug.DrawLine(c0, c1, col); Debug.DrawLine(c1, c2, col);
        Debug.DrawLine(c2, c3, col); Debug.DrawLine(c3, c0, col);
    }

    private void UpdateDetection()
    {
        BallVisible        = false;
        HorizontalOffset   = 0f;
        NormalizedDistance = 1f;

        if (targetBall != null && cam != null)
        {
            // Проекция 3D-точки мяча в кадр камеры: vp.x, vp.y в 0..1, vp.z — глубина
            Vector3 vp = cam.WorldToViewportPoint(targetBall.position);

            // Мяч в кадре (в пирамиде камеры): перед камерой и внутри границ кадра
            bool inFrame = vp.z > 0f
                        && vp.x >= 0f && vp.x <= 1f
                        && vp.y >= 0f && vp.y <= 1f;

            float dist    = Vector3.Distance(cam.transform.position, targetBall.position);
            bool  inRange = dist <= maxRange;

            // Мяч не загорожен стеной
            bool clear = true;
            if (inFrame && inRange)
            {
                Vector3 dir = (targetBall.position - cam.transform.position).normalized;
                if (Physics.Raycast(cam.transform.position, dir, out RaycastHit hit,
                                    dist - 0.01f, obstacleMask, QueryTriggerInteraction.Ignore))
                {
                    clear = false;
                }

                if (drawRays)
                    Debug.DrawLine(cam.transform.position, targetBall.position,
                                   clear ? Color.magenta : Color.gray);
            }

            if (inFrame && inRange && clear)
            {
                BallVisible        = true;
                HorizontalOffset   = Mathf.Clamp((vp.x - 0.5f) * 2f, -1f, 1f);
                NormalizedDistance = Mathf.Clamp01(dist / maxRange);
            }
        }

        if (BallVisible)
        {
            LastKnownOffset   = HorizontalOffset;
            TimeSinceLastSeen = 0f;
        }
        else
        {
            TimeSinceLastSeen += Time.fixedDeltaTime;
        }
    }

    // Диагностика геометрии обзора: с какой дистанции мяч на полу вообще попадает
    // в кадр. Мяч ниже камеры, поэтому вблизи он уходит ЗА НИЖНЮЮ границу кадра —
    // и робот слепнет ровно там, где нужно хватать (gripperRange = 0.08 м).
    // nearLimit должен быть заметно МЕНЬШЕ дальности захвата, иначе робот
    // физически не видит цель в момент захвата.
    public void GetVisibilityRange(float ballY, out float nearLimit, out float farLimit)
    {
        nearLimit = 0f;
        farLimit  = maxRange;
        if (cam == null) return;

        float h = cam.transform.position.y - ballY;            // высота камеры над мячом
        if (h <= 0f) return;                                    // камера ниже мяча — вырожденный случай

        float depress = Mathf.Asin(Mathf.Clamp(-cam.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        float halfV   = verticalFovDeg * 0.5f;

        float lower = depress + halfV;                          // нижний край кадра
        nearLimit = (lower > 0.01f) ? h / Mathf.Tan(lower * Mathf.Deg2Rad) : Mathf.Infinity;

        float upper = depress - halfV;                          // верхний край кадра
        farLimit = (upper > 0.01f)
            ? Mathf.Min(maxRange, h / Mathf.Tan(upper * Mathf.Deg2Rad))
            : maxRange;                                         // горизонт в кадре -> ограничивает только maxRange
    }

    void OnGUI()
    {
        if (!drawRays) return;
        var s = new GUIStyle { fontSize = 16, normal = { textColor = Color.white } };
        GUI.Label(new Rect(10, 124, 600, 22),
            BallVisible
              ? $"МЯЧ ВИДЕН   offset = {HorizontalOffset:F2}   dist = {NormalizedDistance:F2}"
              : $"мяч не виден   last = {LastKnownOffset:F2}   t = {TimeSinceLastSeen:F1} с", s);

        if (targetBall != null)
        {
            GetVisibilityRange(targetBall.position.y, out float n, out float f);
            GUI.Label(new Rect(10, 146, 600, 22),
                $"обзор: мяч в кадре с {n:F2} м по {f:F2} м   (слепая зона: 0..{n:F2} м)", s);
        }
    }
}
