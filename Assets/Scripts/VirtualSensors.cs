using UnityEngine;

public class VirtualSensors : MonoBehaviour
{
    [Header("Точки-якоря")]
    [SerializeField] private Transform centerPoint;
    [SerializeField] private Transform leftIRPoint;
    [SerializeField] private Transform rightIRPoint;
    [SerializeField] private Transform gripperIRPoint;

    [Header("Маски слоёв")]
    [SerializeField] private LayerMask obstacleMask;
    [SerializeField] private LayerMask ballMask;

    [Header("Ультразвук (HC-SR04)")]
    [SerializeField] private float usMaxRange  = 5.0f;
    [SerializeField] private float usConeAngle = 30f; 
    [SerializeField] private int   usRayCount  = 5;

    [Header("ИК")]
    [SerializeField] private float irRange      = 0.15f;
    [SerializeField] private float gripperRange = 0.08f;
    [SerializeField] private float gripperConeDeg = 50f;  // клешня "видит" мяч только СПЕРЕДИ в этом конусе (против боковых захватов)

    [Header("Отладка")]
    [SerializeField] private bool drawRays = true;

    
    public float Ultrasonic { get; private set; }
    public float LeftIR     { get; private set; }
    public float RightIR    { get; private set; }
    public float GripperIR  { get; private set; }

    public GameObject LastSeenBall
	{
		get;
		private set;
	}

    void FixedUpdate()
    {
        Refresh();
    }

    public void ResetCachedState()
    {
        Ultrasonic = 1f;
        LeftIR = 0f;
        RightIR = 0f;
        GripperIR = 0f;
        LastSeenBall = null;
    }

    public void Refresh()
    {
        Ultrasonic = ReadUltrasonic();
        LeftIR     = ReadIR(leftIRPoint);
        RightIR    = ReadIR(rightIRPoint);
        GripperIR  = ReadGripperIR();
    }


    private float ReadUltrasonic()
    {
		
        if (centerPoint == null)
		{
			return 1f;
		}

        float best = usMaxRange;

        for (int i = 0; i < usRayCount; i++)
        {
            float t = (usRayCount == 1) ? 0.5f : (float) i / (usRayCount - 1);
            float angle = Mathf.Lerp(-usConeAngle * 0.5f, usConeAngle * 0.5f, t);

            Vector3 dir = Quaternion.AngleAxis(angle, centerPoint.up) * centerPoint.forward;

            bool didHit = Physics.Raycast(centerPoint.position, dir, out RaycastHit hit, usMaxRange, obstacleMask, QueryTriggerInteraction.Ignore);
            if (didHit)
            {
				
                if (hit.distance < best)
				{
					best = hit.distance;
				}
            }

            if (drawRays)
			{
				Debug.DrawRay(centerPoint.position, didHit ? dir * hit.distance : dir * usMaxRange, didHit ? Color.red : Color.cyan);
			}
                
        }

        return Mathf.Clamp01(best / usMaxRange);
    }

    private float ReadIR(Transform p)
    {
        if (p == null) return 0f;

        bool hit = Physics.Raycast(p.position, p.forward, irRange, obstacleMask, QueryTriggerInteraction.Ignore);

        if (drawRays)
            Debug.DrawRay(p.position, p.forward * irRange, hit ? Color.red : Color.green);

        return hit ? 1f : 0f;
    }

    private float ReadGripperIR()
    {
        LastSeenBall = null;
        if (gripperIRPoint == null) return 0f;

        bool found = false;

        if (Physics.Raycast(gripperIRPoint.position, gripperIRPoint.forward,
                            out RaycastHit hit, gripperRange, ballMask,
                            QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.CompareTag("TargetBall"))
            {
                LastSeenBall = hit.collider.gameObject;
                found = true;
            }
        }

        if (!found)
        {
            var cols = Physics.OverlapSphere(gripperIRPoint.position, gripperRange,
                                             ballMask, QueryTriggerInteraction.Ignore);
            foreach (var c in cols)
            {
                if (!c.CompareTag("TargetBall")) continue;
                // Только если мяч СПЕРЕДИ от клешни (в конусе), а не сбоку —
                // иначе робот "захватывает" мяч, проезжая мимо боком.
                Vector3 toBall = c.transform.position - gripperIRPoint.position;
                if (Vector3.Angle(gripperIRPoint.forward, toBall) > gripperConeDeg * 0.5f) continue;
                LastSeenBall = c.gameObject;
                found = true;
                break;
            }
        }

        if (drawRays)
            Debug.DrawRay(gripperIRPoint.position, gripperIRPoint.forward * gripperRange, found ? Color.yellow : Color.gray);

        return found ? 1f : 0f;
    }

    void OnGUI()
    {
        if (!drawRays) return;
        var s = new GUIStyle { fontSize = 16, normal = { textColor = Color.white } };
        GUI.Label(new Rect(10, 80, 500, 22),
            $"УЗ = {Ultrasonic:F2}   ИК Л = {LeftIR:F0}   ИК П = {RightIR:F0}   Клешня = {GripperIR:F0}", s);
    }
}
