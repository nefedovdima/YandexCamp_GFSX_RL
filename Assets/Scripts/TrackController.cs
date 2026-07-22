using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[RequireComponent(typeof(Rigidbody))]
public class TrackController : MonoBehaviour
{
    [Header("Скорости")]
    [SerializeField] private float moveSpeed = 0.57f;
    [SerializeField] private float turnSpeed = 120f;
    [SerializeField] private float turnK = 0.30f;
    [SerializeField] private float maxLinearCmd = 0.25f;

    [Header("Моторы (PWM 0..100 %)")]
    [SerializeField] private float pwmScale     = 200f;
    [SerializeField] private float motorDeadzone = 10f;
    [SerializeField] private float minMotorPwm  = 35f;
    [SerializeField] private float maxPwmStep   = 15f;

    [Header("Отладка")]
    [SerializeField] private bool showDebug = true;

    [Header("Внешнее управление (ML-Agents)")]
    [SerializeField] private bool externalControl = false; // true = слушаем не клавиатуру, а SetDriveCommand

    private Rigidbody rb;
    private float gas, steer;
    private float pwmL, pwmR;

    private Vector3 Fwd => transform.right;

    // Включает/выключает ручное управление с клавиатуры. RobotBrain ставит true.
    public bool ExternalControl
    {
        get => externalControl;
        set => externalControl = value;
    }

    // Скорость корпуса, м/с — нужна RobotBrain для наблюдения №14.
    // ВАЖНО: rb.linearVelocity здесь всегда ~0, потому что мы двигаем робота через
    // MovePosition (телепорт для некинематического тела). Поэтому отдаём фактическую
    // скорость, рассчитанную из эффективных PWM в FixedUpdate.
    public float CurrentSpeed => currentLinearSpeed;

    // ---------- Доступ для Domain Randomization (Практика 5) ----------
    // RobotBrain меняет эти параметры в начале каждого эпизода обучения,
    // имитируя разброс характеристик реальных моторов и редукторов.
    public float MoveSpeed    { get => moveSpeed;    set => moveSpeed    = value; }
    public float TurnSpeed    { get => turnSpeed;    set => turnSpeed    = value; }
    public float MaxPwmStep   { get => maxPwmStep;   set => maxPwmStep   = value; }
    public float MaxLinearCmd { get => maxLinearCmd; set => maxLinearCmd = value; }

    private float currentLinearSpeed;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.linearDamping  = 8f;
        rb.angularDamping = 8f;
        rb.centerOfMass   = new Vector3(0f, 0.03f, 0f);
    }

    void Update() => ReadInput();

    // Вызывается агентом вместо клавиатуры: gasCmd, steerCmd в диапазоне -1..1
    public void SetDriveCommand(float gasCmd, float steerCmd)
    {
        gas   = Mathf.Clamp(gasCmd, -1f, 1f);
        steer = Mathf.Clamp(steerCmd, -1f, 1f);
    }

    private void ReadInput()
    {
        if (externalControl) return; // команды уже пришли через SetDriveCommand

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null)
		{
			gas = 0f;
			steer = 0f;
			return;
		}

        gas = 0f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed)
		{
			gas += 1f;
		}
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed)
		{
			gas -= 1f;
		}

        steer = 0f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed)
		{
			steer += 1f;
		}
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)
		{
			steer -= 1f;
		}
#else
        gas   = Input.GetAxisRaw("Vertical");
        steer = Input.GetAxisRaw("Horizontal");
#endif
    }

    void FixedUpdate()
    {
        float v  = Mathf.Clamp(gas * moveSpeed, -maxLinearCmd, maxLinearCmd);
        float dv = steer * turnK;

        float vL = v - dv;
        float vR = v + dv;

        pwmL = Mathf.MoveTowards(pwmL, SpeedToPwm(vL), maxPwmStep);
        pwmR = Mathf.MoveTowards(pwmR, SpeedToPwm(vR), maxPwmStep);

        float effL = pwmL / pwmScale;
        float effR = pwmR / pwmScale;

        float linear = (effL + effR) * 0.5f;
        currentLinearSpeed = Mathf.Abs(linear);

        float turnNorm = (turnK > 1e-4f)
            ? Mathf.Clamp((effR - effL) / (2f * turnK), -1f, 1f)
            : 0f;
        float yawRate = turnNorm * turnSpeed;

        float dt = Time.fixedDeltaTime;
        rb.MovePosition(rb.position + Fwd * (linear * dt));
        rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, yawRate * dt, 0f));
    }

    private float SpeedToPwm(float trackSpeed)
    {
        float raw = Mathf.Abs(trackSpeed) * pwmScale;

        if (raw < motorDeadzone) return 0f;

        float pwm = Mathf.Clamp(Mathf.Max(raw, minMotorPwm), 0f, 100f);
        return pwm * Mathf.Sign(trackSpeed);
    }

    void OnGUI()
    {
        if (!showDebug) return;
        var s = new GUIStyle
		{
			fontSize = 16,
			normal =
			{
				textColor = Color.white
			}
		};
        GUI.Label(new Rect(10, 10, 400, 22), $"gas = {gas:F2}    steer = {steer:F2}", s);
        GUI.Label(new Rect(10, 32, 400, 22), $"PWM   L = {pwmL:F0} %   R = {pwmR:F0} %", s);
        GUI.Label(new Rect(10, 54, 400, 22), $"|v| = {rb.linearVelocity.magnitude:F3} м/с", s);
    }
}
