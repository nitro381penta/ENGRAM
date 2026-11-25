using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Canvas))]
public class EngramTableAnchoredUI : MonoBehaviour
{
    [Header("Engram References")]
    [SerializeField] private EngramTableReference tableReference;
    [SerializeField] private EngramSpatialInventory spatialInventory;
    [SerializeField] private RectTransform uiCanvas;
    [SerializeField] private Transform centerEyeAnchor;

    [Header("Placement")]
    [SerializeField] private bool waitUntilObjectsDocked = true;

    [SerializeField, Min(0f)]
    private float distanceBehindTable = 0.45f;

    [SerializeField, Min(0f)]
    private float centerHeightAboveTable = 0.55f;

    [SerializeField]
    private float horizontalOffset = 0f;

    [Header("Orientation")]
    [SerializeField]
    private float yawOffsetDegrees = 0f;

    [SerializeField]
    private bool reverseFacing = false;

    [Header("Calibration")]
    [SerializeField]
    private bool recalibrateDirectionOnStart = false;

    private Canvas canvasComponent;

    private bool directionInitialized;
    private float savedLocalYaw;

    private string activeAnchorKey;

    private void Awake()
    {
        if (uiCanvas == null)
            uiCanvas = GetComponent<RectTransform>();

        canvasComponent = GetComponent<Canvas>();
    }

    private void OnEnable()
    {
        if (tableReference != null)
        {
            tableReference.ReferenceReady += OnReferenceReady;
            tableReference.ReferenceCleared += OnReferenceCleared;
        }
    }

    private void Start()
    {
        if (canvasComponent != null)
            canvasComponent.enabled = false;
    }

    private void LateUpdate()
    {
        if (tableReference == null ||
            !tableReference.HasReference ||
            uiCanvas == null)
        {
            SetCanvasVisible(false);
            return;
        }

        if (waitUntilObjectsDocked &&
            (spatialInventory == null ||
             !spatialInventory.ObjectsDocked))
        {
            SetCanvasVisible(false);
            return;
        }

        Transform anchor = tableReference.AnchorTransform;

        if (anchor == null)
        {
            SetCanvasVisible(false);
            return;
        }

        string key = GetCalibrationKey();

        if (!directionInitialized || activeAnchorKey != key)
        {
            directionInitialized = false;

            if (!InitializeDirection(anchor, key))
            {
                SetCanvasVisible(false);
                return;
            }
        }

        UpdateCanvasPosition(anchor);

        SetCanvasVisible(true);
    }

    private bool InitializeDirection(
        Transform anchor,
        string key)
    {
        activeAnchorKey = key;

        if (!recalibrateDirectionOnStart &&
            PlayerPrefs.HasKey(key))
        {
            savedLocalYaw = PlayerPrefs.GetFloat(key);

            directionInitialized = true;

            Debug.Log(
                "Engram UI: Restored saved table orientation."
            );

            return true;
        }

        if (centerEyeAnchor == null)
        {
            Camera mainCamera = Camera.main;

            if (mainCamera == null)
                return false;

            centerEyeAnchor = mainCamera.transform;
        }

        // Determine the horizontal direction from viewer to table.
        Vector3 forward = Vector3.ProjectOnPlane(
            anchor.position - centerEyeAnchor.position,
            Vector3.up
        );

        if (forward.sqrMagnitude < 0.01f)
        {
            forward = Vector3.ProjectOnPlane(
                centerEyeAnchor.forward,
                Vector3.up
            );
        }

        if (forward.sqrMagnitude < 0.001f)
            return false;

        forward.Normalize();

        // Store the direction relative to the table anchor.
        Vector3 localForward =
            anchor.InverseTransformDirection(forward);

        localForward.y = 0f;

        if (localForward.sqrMagnitude < 0.001f)
            return false;

        localForward.Normalize();

        savedLocalYaw = Mathf.Atan2(
            localForward.x,
            localForward.z
        ) * Mathf.Rad2Deg;

        PlayerPrefs.SetFloat(key, savedLocalYaw);
        PlayerPrefs.Save();

        directionInitialized = true;

        Debug.Log(
            $"Engram UI: Table orientation calibrated: " +
            $"{savedLocalYaw:F1} degrees."
        );

        return true;
    }

    private void UpdateCanvasPosition(Transform anchor)
    {
        // Restore the saved direction in anchor coordinates.
        Quaternion localRotation = Quaternion.Euler(
            0f,
            savedLocalYaw + yawOffsetDegrees,
            0f
        );

        Vector3 localForward =
            localRotation * Vector3.forward;

        Vector3 forward = anchor.TransformDirection(
            localForward
        );

        forward = Vector3.ProjectOnPlane(
            forward,
            Vector3.up
        );

        if (forward.sqrMagnitude < 0.001f)
            return;

        forward.Normalize();

        Vector3 right = Vector3.Cross(
            Vector3.up,
            forward
        ).normalized;

        // Position the UI behind the table reference.
        Vector3 targetPosition =
            anchor.position +
            forward * distanceBehindTable +
            right * horizontalOffset;

        // Use an explicit height above the tabletop.
        targetPosition.y =
            tableReference.TableHeight +
            centerHeightAboveTable;

        Vector3 facing = reverseFacing
            ? -forward
            : forward;

        // Keep the UI vertical without pitch or roll.
        Quaternion targetRotation = Quaternion.LookRotation(
            facing,
            Vector3.up
        );

        uiCanvas.SetPositionAndRotation(
            targetPosition,
            targetRotation
        );
    }

    private string GetCalibrationKey()
    {
        return "Engram.MemoryUI.Direction." +
               tableReference.AnchorUuid.ToString("N");
    }

    [ContextMenu("Engram/Recalibrate UI Direction")]
    public void RecalibrateDirection()
    {
        if (tableReference == null ||
            !tableReference.HasReference)
        {
            return;
        }

        string key = GetCalibrationKey();

        PlayerPrefs.DeleteKey(key);
        PlayerPrefs.Save();

        directionInitialized = false;
        activeAnchorKey = null;

        Debug.Log(
            "Engram UI: Saved orientation cleared."
        );
    }

    private void OnReferenceReady()
    {
        directionInitialized = false;
    }

    private void OnReferenceCleared()
    {
        directionInitialized = false;
        activeAnchorKey = null;

        SetCanvasVisible(false);
    }

    private void SetCanvasVisible(bool visible)
    {
        if (canvasComponent != null &&
            canvasComponent.enabled != visible)
        {
            canvasComponent.enabled = visible;
        }
    }

    private void OnDisable()
    {
        if (tableReference != null)
        {
            tableReference.ReferenceReady -= OnReferenceReady;
            tableReference.ReferenceCleared -= OnReferenceCleared;
        }
    }
}