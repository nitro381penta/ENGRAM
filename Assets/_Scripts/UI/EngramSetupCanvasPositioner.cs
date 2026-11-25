using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Canvas))]
public class EngramSetupCanvasPositioner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Head / CenterEyeAnchor of the Camera Rig. Uses Camera.main as fallback.")]
    [SerializeField] private Transform headTransform;
    [SerializeField] private EngramSessionFlow sessionFlow;

    [Header("One-Time Placement")]
    [SerializeField, Min(0.5f)] private float distanceFromHead = 1.15f;
    [SerializeField] private float verticalOffset = -0.05f;
    [SerializeField, Min(0f)] private float trackingReadyDelay = 0.15f;

    private CanvasGroup canvasGroup;
    private float originalAlpha;
    private bool originalInteractable;
    private bool originalBlocksRaycasts;
    private Coroutine placementRoutine;

    private void Awake()
    {
        if (sessionFlow == null)
            sessionFlow = FindFirstObjectByType<EngramSessionFlow>();

        canvasGroup = GetComponent<CanvasGroup>();
        if (canvasGroup == null)
            canvasGroup = gameObject.AddComponent<CanvasGroup>();

        originalAlpha = canvasGroup.alpha;
        originalInteractable = canvasGroup.interactable;
        originalBlocksRaycasts = canvasGroup.blocksRaycasts;
        HideUntilPlaced();
    }

    private void OnEnable()
    {
        if (sessionFlow != null)
            sessionFlow.SetupOpened += Recenter;
    }

    private void Start()
    {
        // Fallback if the flow opened before this script subscribed.
        Recenter();
    }

    // Called when the introduction appears initially or after a new run.
    public void Recenter()
    {
        if (!isActiveAndEnabled)
            return;

        if (placementRoutine != null)
            StopCoroutine(placementRoutine);

        HideUntilPlaced();
        placementRoutine = StartCoroutine(PositionWhenReady());
    }

    private IEnumerator PositionWhenReady()
    {
        // Give the Quest tracking origin a moment to initialise.
        yield return null;
        if (trackingReadyDelay > 0f)
            yield return new WaitForSecondsRealtime(trackingReadyDelay);

        Transform head = headTransform;
        if (head == null && Camera.main != null)
            head = Camera.main.transform;

        if (head == null)
        {
            Debug.LogError("Engram Setup: Assign Head Transform (CenterEyeAnchor) to the canvas positioner.");
            ShowCanvas();
            placementRoutine = null;
            yield break;
        }

        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
        }

        forward.Normalize();

        // A fixed world-space pose, not parented to the head or table anchor.
        Vector3 position = head.position + forward * distanceFromHead;
        position.y += verticalOffset;
        transform.SetPositionAndRotation(
            position,
            Quaternion.LookRotation(forward, Vector3.up));

        ShowCanvas();
        placementRoutine = null;
    }

    private void HideUntilPlaced()
    {
        if (canvasGroup == null)
            return;

        canvasGroup.alpha = 0f;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;
    }

    private void ShowCanvas()
    {
        if (canvasGroup == null)
            return;

        canvasGroup.alpha = originalAlpha;
        canvasGroup.interactable = originalInteractable;
        canvasGroup.blocksRaycasts = originalBlocksRaycasts;
    }

    private void OnDisable()
    {
        if (sessionFlow != null)
            sessionFlow.SetupOpened -= Recenter;

        if (placementRoutine != null)
        {
            StopCoroutine(placementRoutine);
            placementRoutine = null;
        }

        ShowCanvas();
    }

}
