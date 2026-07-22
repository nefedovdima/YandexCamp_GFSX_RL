using UnityEngine;
using Unity.MLAgents;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry; // TwistMsg
using RosMessageTypes.Std;      // Int32Msg, Float32Msg

// Мост Unity -> ROS (Практика 3, шаг 8).
// Транслирует решения обученной сети в топики ROS на Raspberry Pi робота по TCP.
// Работает ТОЛЬКО на инференсе: во время обучения полностью отключается (см. Start).
//
// Три отличия от шаблона методички — намеренные, см. подробности у соответствующих полей:
//   1. Знак angular.z инвертирован (ROS REP-103 против левосторонних координат Unity).
//   2. Watchdog (обязательная доработка из P3) + периодический перепосыл стопа.
//   3. Мост сам себя выключает на обучении, чтобы не долбиться в несуществующий ROS.
//
// ⚠️ ГРАНИЦА ОТВЕТСТВЕННОСТИ WATCHDOG'А — прочитайте до выезда на полигон.
// Задание P3 формулирует проблему как «оборвётся Wi-Fi или зависнет Unity». Watchdog
// ЗДЕСЬ ни от того, ни от другого не спасает, и это не лечится:
//   * зависло Unity  -> не крутится Update() -> стоп физически некому отправить;
//   * оборвался Wi-Fi -> стоп отправлен, но не доехал до Pi.
// В обоих случаях нода на Pi продолжит крутить последнюю скорость.
// Отсюда здесь спасает только одно: агент жив, а команды слать перестал (заминка
// инференса, конец эпизода, отвалившийся ONNX).
// НАСТОЯЩИЙ fail-safe обязан жить НА СТОРОНЕ Pi: нода /cmd_vel должна сама глушить
// моторы, если сообщений нет N мс (обычно 200-500). Unity-watchdog — второй эшелон,
// а не первый. Без таймаута на Pi робот остаётся небезопасным.
public class ROSBridge : MonoBehaviour
{
    [Header("Топики")]
    [SerializeField] private string cmdVelTopic    = "/cmd_vel";
    [SerializeField] private string gripperTopic   = "/cmd_gripper";
    [SerializeField] private string cameraPanTopic = "/cmd_camera_pan";

    [Header("Лимиты реального робота")]
    [SerializeField] private float maxLinearSpeed  = 0.5f;  // м/с
    [SerializeField] private float maxAngularSpeed = 1.0f;  // рад/с

    // ROS REP-103: ось Z смотрит вверх в ПРАВОЙ системе координат, поэтому
    // angular.z > 0 — это поворот ПРОТИВ часовой стрелки (влево).
    // Unity — ЛЕВОСТОРОННЯЯ система: в TrackController steer > 0 даёт effR > effL,
    // yawRate > 0 и поворот ПО часовой стрелке (вправо, клавиша D).
    // Значит steer > 0 (вправо) обязан уехать в ROS как angular.z < 0.
    // В шаблоне методички знак не инвертирован — робот на полигоне будет
    // поворачивать в ЗЕРКАЛЬНУЮ сторону.
    // ⚠️ Проверьте на стенде (робот на подставке, колёса в воздухе) до первого выезда:
    // если ваша ROS-нода на Pi трактует angular.z как «руль вправо», снимите галку.
    [SerializeField] private bool invertAngularForRos = true;

    [Header("Сглаживание (EMA)")]
    // 0.8 = высокая отзывчивость: срезает дребезг сети, но сохраняет реакцию на резкий поворот.
    // Без него моторы гудят, греются и редуктор изнашивается.
    [Range(0.1f, 1f)]
    [SerializeField] private float emaAlpha = 0.8f;

    [Header("Watchdog (fail-safe)")]
    // Если агент не присылал команд дольше этого времени — шлём аварийный стоп.
    [SerializeField] private float watchdogTimeout = 0.5f;
    // Пока watchdog взведён, стоп перепосылается с этим интервалом: одиночный UDP/TCP-пакет
    // может потеряться, а «робот не получил стоп» — самый дорогой из возможных исходов.
    [SerializeField] private float stopResendInterval = 0.1f;

    [Header("Отладка")]
    [SerializeField] private bool verbose = true;

    private ROSConnection ros;
    private float smoothGas;
    private float smoothSteering;
    private float lastCommandTime;
    private float lastStopSentTime;
    private bool  watchdogTripped;
    private bool  active;   // мост поднят и публикует

    public bool IsActive => active;

    void Start()
    {
        // На обучении моста быть не должно. Иначе каждая из --num-envs копий полезет
        // открывать TCP-соединение к несуществующему ROS-хосту, засыплет лог ошибками
        // и будет жечь CPU на переподключениях.
        if (Academy.IsInitialized && Academy.Instance.IsCommunicatorOn)
        {
            if (verbose) Debug.Log("[ROSBridge] Обучение: мост отключён.");
            enabled = false;
            return;
        }

        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<TwistMsg>(cmdVelTopic);
        ros.RegisterPublisher<Int32Msg>(gripperTopic);
        ros.RegisterPublisher<Float32Msg>(cameraPanTopic);

        active          = true;
        lastCommandTime = Time.time;

        if (verbose) Debug.Log($"[ROSBridge] Инференс: публикую в {cmdVelTopic}, {gripperTopic}, {cameraPanTopic}.");
    }

    // ---------- Публикация скоростей ----------
    // Вызывается агентом каждый шаг инференса. gas/steering в диапазоне -1..1.
    public void PublishCommand(float gas, float steering)
    {
        if (!active) return;

        lastCommandTime = Time.time;

        // Watchdog «отпускаем»: команды снова идут.
        if (watchdogTripped)
        {
            watchdogTripped = false;
            if (verbose) Debug.Log("[ROSBridge] Watchdog снят, команды пошли.");
        }

        gas      = Mathf.Clamp(gas,      -1f, 1f);
        steering = Mathf.Clamp(steering, -1f, 1f);

        // Hard Stop: чистый ноль сбрасывает фильтр мгновенно, без затухания.
        // Иначе «ошмётки» EMA продолжают ползти и робот паразитно дрейфует.
        if (Mathf.Approximately(gas, 0f) && Mathf.Approximately(steering, 0f))
        {
            smoothGas      = 0f;
            smoothSteering = 0f;
        }
        else
        {
            smoothGas      = emaAlpha * gas      + (1f - emaAlpha) * smoothGas;
            smoothSteering = emaAlpha * steering + (1f - emaAlpha) * smoothSteering;
        }

        SendTwist(smoothGas * maxLinearSpeed, smoothSteering * maxAngularSpeed);
    }

    // ---------- Клешня ----------
    // cmd: 0 = стоять, 1 = закрыть, 2 = открыть (та же кодировка, что у DiscreteActions[0])
    public void PublishGripperCmd(int cmd)
    {
        if (!active) return;
        ros.Publish(gripperTopic, new Int32Msg { data = cmd });
    }

    // ---------- Сервопривод камеры (порт S5, панорамирование) ----------
    // ВНИМАНИЕ: принимает УГОЛ В ГРАДУСАХ (-camServoMaxAngle..+camServoMaxAngle),
    // а не сырое действие сети -1..1. Действие сети — это СКОРОСТЬ поворота, а серво
    // ждёт ПОЗИЦИЮ. RobotBrain интегрирует действие в camServoAngle — его и шлём.
    // В шаблоне методички публикуется сырое действие, это рассогласование.
    public void PublishCameraCmd(float yawDegrees)
    {
        if (!active) return;
        ros.Publish(cameraPanTopic, new Float32Msg(yawDegrees));
    }

    // ---------- Watchdog ----------
    void Update()
    {
        if (!active) return;

        if (Time.time - lastCommandTime <= watchdogTimeout) return;

        if (!watchdogTripped)
        {
            watchdogTripped  = true;
            smoothGas        = 0f;
            smoothSteering   = 0f;
            lastStopSentTime = float.NegativeInfinity; // отправить стоп немедленно
            Debug.LogWarning(
                $"[ROSBridge] WATCHDOG: команд нет {watchdogTimeout:F2} с — аварийный стоп.");
        }

        // Держим стоп, а не шлём один раз: пакет мог не дойти.
        if (Time.time - lastStopSentTime >= stopResendInterval)
        {
            lastStopSentTime = Time.time;
            SendTwist(0f, 0f);
        }
    }

    private void SendTwist(float linear, float angular)
    {
        var cmd = new TwistMsg();
        cmd.linear.x  = linear;
        cmd.angular.z = invertAngularForRos ? -angular : angular;
        ros.Publish(cmdVelTopic, cmd);
    }

    // Аварийный стоп «снаружи» (например, по кнопке оператора).
    public void EmergencyStop()
    {
        if (!active) return;
        smoothGas      = 0f;
        smoothSteering = 0f;
        SendTwist(0f, 0f);
        Debug.LogWarning("[ROSBridge] Аварийный стоп по внешнему вызову.");
    }

    // Приложение закрывают/останавливают Play — не оставляем робота в движении.
    void OnDisable()
    {
        if (active && ros != null) SendTwist(0f, 0f);
    }
}
