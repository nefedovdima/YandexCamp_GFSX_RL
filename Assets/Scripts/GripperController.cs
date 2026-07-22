/// Мы одновременно подключаем библиотеку UnityEngine и активируем её простравнство имён (using namespace).
/// В отличие от cpp c# активируем только указанное пространство имён, но не вложенные
using UnityEngine;
/// Активируем её простравнство имён UnityEngine.InputSystem (using namespace) (без неё придётся писать UnityEngine.InputSystem).
using UnityEngine.InputSystem;

/// Класс GripperController является прямым наследником MonoBehaviour
public class GripperController : MonoBehaviour
{
    [Header("Ссылки")] // Аттрибут Header(arg) из UnityEngine.HeaderAttribute, выводящий аргумент поверх блока-компонента
    // Аттрибут SerializeField из UnityEngine.SerializeField, означающий, что значение вводится пользователем
    // После мы объявляет private поле класса
    /*
    public class Transform : Component
    {
        public Vector3 position;         // Позиция в мировом пространстве (x, y, z)
        public Quaternion rotation;      // Поворот в мировом пространстве
        public Vector3 localPosition;    // Позиция относительно родителя
        public Quaternion localRotation; // Поворот относительно родителя
        public Vector3 localScale;       // Масштаб, в метрах
        
        public Transform parent;        // Родитель
        public int childCount;          // Количество дочерних объектов
        
        // Методы
        public void SetParent(Transform parent);  // Установить нового единственного родителя
        public void Rotate(Vector3 eulerAngles);  // К текущему углу поворота поэлементно прибаляет поданный
        public void Translate(Vector3 direction); // К текущей позиции поэлементно прибавляет поданную
    }
    */
    [SerializeField] private Transform holdPoint;
    
    //Мой пользовательский класс, 
    [SerializeField] private VirtualSensors sensors;
    [SerializeField] private Rigidbody robotBody;

    [Header("Состояние")]
    [SerializeField] private bool isHolding = false;

    [Header("Внешнее управление (ML-Agents)")]
    [SerializeField] private bool externalControl = false; // true = игнорируем пробел, ждём Grab()/ReleaseBall()

    private Rigidbody heldRb;
    private Collider  heldCol;

    // Захвачен ли мяч сейчас — нужно RobotBrain для наблюдения №10 и наград
    public bool IsHolding => isHolding;

    public bool ExternalControl
    {
        get => externalControl;
        set => externalControl = value;
    }

    void Update()
    {
        if (externalControl) return; // команды идут через Grab()/ReleaseBall()

        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.spaceKey.wasPressedThisFrame)
        {
            if (isHolding) Release();
            else           TryGrab();
        }
    }

    // Публичная обёртка для RobotBrain: попытаться схватить мяч
    public void Grab() => TryGrab();

    // Публичная обёртка для RobotBrain: отпустить мяч
    public void ReleaseBall() => Release();

    private void TryGrab()
    {
        if (sensors == null || holdPoint == null) return;
        if (sensors.GripperIR < 0.5f) return;

        GameObject ball = sensors.LastSeenBall;
        if (ball == null) return;

        heldRb  = ball.GetComponent<Rigidbody>();
        heldCol = ball.GetComponent<Collider>();
        if (heldRb == null || heldCol == null) return;

        heldRb.isKinematic = true;
		heldRb.interpolation = RigidbodyInterpolation.None;
        heldCol.enabled = false;

        ball.transform.SetParent(holdPoint);
        ball.transform.localPosition = Vector3.zero;
        ball.transform.localRotation = Quaternion.identity;

        isHolding = true;
        Debug.Log("Захват");
    }

    private void Release()
    {
        if (!isHolding || heldRb == null) return;

        heldRb.transform.SetParent(null);
        heldCol.enabled = true;
        heldRb.isKinematic = false;

        if (robotBody != null)
            heldRb.linearVelocity = robotBody.linearVelocity;

        heldRb  = null;
        heldCol = null;
        isHolding = false;
        Debug.Log("Отпуск");
    }

    void OnGUI()
    {
        var s = new GUIStyle { fontSize = 16, normal = { textColor = Color.white } };
        GUI.Label(new Rect(10, 102, 500, 22),
            isHolding ? "МЯЧ В КЛЕШНЕ (Space — отпустить)" : "Space — схватить", s);
    }
}
