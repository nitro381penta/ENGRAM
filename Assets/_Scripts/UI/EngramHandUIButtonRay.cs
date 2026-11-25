using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class EngramHandUIButtonRay : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Canvas uiCanvas;
    [Tooltip("Optional separate World Space Canvas for the three setup pages.")]
    [SerializeField] private Canvas setupCanvas;
    [SerializeField] private Button resetButton;
    [SerializeField] private Button finishButton;

    [Header("Session")]
    [SerializeField] private EngramSessionFlow sessionFlow;
    [SerializeField] private EngramVoiceManager voiceManager;

    [Header("Recall Objects")]
    [SerializeField] private EngramMemoryRecall memoryRecall;
    [SerializeField] private bool showRayForRecallObjects = true;
    [SerializeField] private bool showPlacementPreviewRay = true;

    [Header("Hands")]
    [SerializeField] private OVRHand leftHand;
    [SerializeField] private OVRHand rightHand;
    [SerializeField] private bool enableHands = true;

    [Header("Controllers")]
    [SerializeField] private Transform leftControllerAnchor;
    [SerializeField] private Transform rightControllerAnchor;
    [SerializeField] private bool enableControllers = true;

    [Header("Input")]
    [SerializeField, Range(0f, 1f)] private float pinchStartThreshold = 0.65f;
    [SerializeField, Range(0f, 1f)] private float pinchReleaseThreshold = 0.35f;
    [SerializeField, Range(0f, 1f)] private float triggerStartThreshold = 0.6f;
    [SerializeField, Range(0f, 1f)] private float triggerReleaseThreshold = 0.3f;
    [SerializeField, Min(0.5f)] private float maximumRayDistance = 4f;

    [Header("Reliable UI Interaction")]
    [SerializeField] private bool enableRightControllerAButton = true;
    [SerializeField, Min(0f)] private float buttonAimPadding = 12f;
    [SerializeField, Range(0f, 0.3f)] private float hoverGraceSeconds = 0.12f;
    [SerializeField, Range(0.1f, 0.8f)] private float clickCooldownSeconds = 0.25f;

    [Header("Pointer Visuals")]
    [SerializeField] private bool showPointer = true;
    [SerializeField, Min(0.0005f)] private float rayWidth = 0.0012f;
    [SerializeField, Range(0.1f, 1f)] private float rayOpacity = 0.85f;
    [SerializeField, Min(0f)] private float rayStartOffset = 0.15f;
    [SerializeField, Min(0f)] private float rayEndGap = 0.012f;
    [SerializeField, Min(2f)] private float dotSize = 12f;
    [SerializeField, Min(0f)] private float dotSurfaceOffset = 0.008f;

    [Header("Activation & Haptics")]
    // New serialized name intentionally resets the old blue tint to turquoise.
    [SerializeField] private Color activationTurquoise =
        new Color(0.12f, 0.96f, 0.83f, 1f);
    [SerializeField, Range(0.05f, 0.5f)] private float activationFeedbackSeconds = 0.18f;
    [SerializeField] private bool enableControllerHaptics = true;
    [SerializeField, Range(0f, 1f)] private float clickHapticAmplitude = 0.35f;
    [SerializeField, Range(0.02f, 0.2f)] private float clickHapticDurationSeconds = 0.055f;

    private sealed class PointerState
    {
        public OVRHand Hand;
        public Transform ControllerAnchor;
        public OVRInput.Controller Controller;
        public bool IsController;
        public bool Pressed;
        public bool WasHoldingObject;
        public Button LastHoveredButton;
        public Vector3 LastHoverPoint;
        public float LastHoverTime = -100f;
        public float LastClickTime = -100f;
        public Vector3 LastObjectAimPoint;
        public bool HasObjectAimPoint;
        public Vector3 ActivationPoint;
        public float ActivationUntil = -100f;
        public float HapticStopTime = -100f;
        public bool HapticPlaying;
        public LineRenderer Line;
        public SpriteRenderer Dot;
    }

    private readonly PointerState leftHandState = new PointerState();
    private readonly PointerState rightHandState = new PointerState();
    private readonly PointerState leftControllerState = new PointerState();
    private readonly PointerState rightControllerState = new PointerState();

    private Graphic[] visibleUiGraphics;
    private GameObject visualRoot;
    private Material rayMaterial;
    private Texture2D dotTexture;
    private Sprite dotSprite;

    private void Awake()
    {
        if (sessionFlow == null)
            sessionFlow = FindFirstObjectByType<EngramSessionFlow>();

        if (voiceManager == null)
            voiceManager = FindFirstObjectByType<EngramVoiceManager>();

        if (memoryRecall == null)
            memoryRecall = FindFirstObjectByType<EngramMemoryRecall>();

        if (uiCanvas == null || uiCanvas.renderMode != RenderMode.WorldSpace)
        {
            Debug.LogError("Engram pointer: assign the existing World Space UI Canvas.");
            enabled = false;
            return;
        }

        var graphics = new List<Graphic>(uiCanvas.GetComponentsInChildren<Graphic>(true));
        if (setupCanvas != null && setupCanvas != uiCanvas)
            graphics.AddRange(setupCanvas.GetComponentsInChildren<Graphic>(true));
        visibleUiGraphics = graphics.ToArray();

        leftHandState.Hand = leftHand;
        rightHandState.Hand = rightHand;

        leftControllerState.IsController = true;
        leftControllerState.Controller = OVRInput.Controller.LTouch;
        leftControllerState.ControllerAnchor = leftControllerAnchor;

        rightControllerState.IsController = true;
        rightControllerState.Controller = OVRInput.Controller.RTouch;
        rightControllerState.ControllerAnchor = rightControllerAnchor;

        visualRoot = new GameObject("EngramPointerVisuals");
        CreateResources();
        CreateVisuals(leftHandState, "LeftHand");
        CreateVisuals(rightHandState, "RightHand");
        CreateVisuals(leftControllerState, "LeftController");
        CreateVisuals(rightControllerState, "RightController");
    }

    private void Update()
    {
        UpdateControllerHaptic(leftControllerState);
        UpdateControllerHaptic(rightControllerState);

        UpdatePointer(leftHandState, enableHands);
        UpdatePointer(rightHandState, enableHands);
        UpdatePointer(leftControllerState, enableControllers);
        UpdatePointer(rightControllerState, enableControllers);
    }

    private void UpdatePointer(PointerState state, bool inputEnabled)
    {
        if (!inputEnabled || !TryGetPointer(state, out Ray ray, out float strength))
        {
            HidePointer(state);
            state.LastHoveredButton = null;
            state.LastHoverTime = -100f;
            state.HasObjectAimPoint = false;
            state.WasHoldingObject = false;
            return;
        }

        bool canvasAvailable =
            (uiCanvas != null && uiCanvas.isActiveAndEnabled) ||
            (setupCanvas != null && setupCanvas.isActiveAndEnabled);
        Button hovered = canvasAvailable
            ? FindHoveredButton(ray, out Vector3 ignoredHitPoint)
            : null;

        // Use the real button plane for the click feedback / cursor position.
        Vector3 buttonPoint = default;
        if (hovered != null)
        {
            TryHitRect(hovered.GetComponent<RectTransform>(), ray,
                buttonAimPadding, out buttonPoint, out float ignoredDistance);
            state.LastHoveredButton = hovered;
            state.LastHoverPoint = buttonPoint;
            state.LastHoverTime = Time.unscaledTime;
        }

        if (enableRightControllerAButton && state.IsController &&
            state.Controller == OVRInput.Controller.RTouch &&
            OVRInput.GetDown(OVRInput.Button.One, state.Controller))
        {
            ActivateButton(state, hovered, buttonPoint);
        }

        float startThreshold = state.IsController ? triggerStartThreshold : pinchStartThreshold;
        float releaseThreshold = state.IsController ? triggerReleaseThreshold : pinchReleaseThreshold;

        if (strength <= releaseThreshold)
        {
            state.Pressed = false;
        }
        else if (!state.Pressed && strength >= startThreshold)
        {
            state.Pressed = true;
            ActivateButton(state, hovered, buttonPoint);
        }

        bool uiTarget = false;
        Vector3 uiPoint = default;
        float uiDistance = float.MaxValue;

        if (canvasAvailable)
        {
            uiTarget = TryGetVisibleUiHit(ray, out uiPoint, out uiDistance);
            if (hovered != null)
            {
                uiTarget = true;
                uiPoint = buttonPoint;
                uiDistance = Vector3.Distance(ray.origin, buttonPoint);
            }
        }

        bool holdingObject = state.IsController && memoryRecall != null &&
            memoryRecall.IsControllerHoldingObject(state.Controller);

        bool objectTarget = false;
        Vector3 objectPoint = default;
        float objectDistance = float.MaxValue;

        if (state.IsController && memoryRecall != null && showRayForRecallObjects)
        {
            if (holdingObject && showPlacementPreviewRay)
            {
                objectTarget = memoryRecall.TryGetControllerPlacementPreviewPoint(
                    state.Controller, out objectPoint);
            }
            else if (!holdingObject)
            {
                objectTarget = memoryRecall.TryGetControllerAimTarget(ray, out objectPoint);
            }

            if (objectTarget)
            {
                objectDistance = Vector3.Distance(ray.origin, objectPoint);
                state.LastObjectAimPoint = objectPoint;
                state.HasObjectAimPoint = true;
            }
        }

        // Confirm an actual controller grab after the recall component acquired it.
        if (holdingObject && !state.WasHoldingObject)
        {
            state.ActivationUntil = Time.unscaledTime + activationFeedbackSeconds;
            state.ActivationPoint = state.HasObjectAimPoint
                ? state.LastObjectAimPoint
                : (objectTarget ? objectPoint : ray.GetPoint(0.5f));
            PlayControllerClickHaptic(state);
        }
        state.WasHoldingObject = holdingObject;

        if (!showPointer)
        {
            HidePointer(state);
            return;
        }

        bool feedbackActive = Time.unscaledTime < state.ActivationUntil;
        if (feedbackActive)
        {
            DrawPointer(state, ray.origin, state.ActivationPoint, true);
            return;
        }

        // A hand pinch used to grab objects must never leave a UI ray visible.
        if (!state.IsController && strength > pinchReleaseThreshold)
        {
            HidePointer(state);
            return;
        }

        if (uiTarget && (!objectTarget || uiDistance <= objectDistance))
        {
            DrawPointer(state, ray.origin, uiPoint, false);
        }
        else if (objectTarget)
        {
            DrawPointer(state, ray.origin, objectPoint, false);
        }
        else
        {
            HidePointer(state);
        }
    }

    private void ActivateButton(PointerState state, Button hovered, Vector3 hoveredPoint)
    {
        Button selected = hovered;
        Vector3 selectedPoint = hoveredPoint;

        if (selected == null &&
            Time.unscaledTime - state.LastHoverTime <= hoverGraceSeconds)
        {
            selected = state.LastHoveredButton;
            selectedPoint = state.LastHoverPoint;
        }

        if (selected == null || !selected.isActiveAndEnabled ||
            !selected.IsInteractable() ||
            Time.unscaledTime - state.LastClickTime < clickCooldownSeconds)
        {
            return;
        }

        state.LastClickTime = Time.unscaledTime;
        state.ActivationUntil = Time.unscaledTime + activationFeedbackSeconds;
        state.ActivationPoint = selectedPoint;
        PlayControllerClickHaptic(state);
        selected.onClick.Invoke();
    }

    private bool TryGetPointer(PointerState state, out Ray ray, out float strength)
    {
        ray = default;
        strength = 0f;

        if (state.IsController)
        {
            if (state.ControllerAnchor == null)
                return false;

            OVRInput.Controller connected = OVRInput.GetConnectedControllers();
            if ((connected & state.Controller) == OVRInput.Controller.None)
                return false;

            // Put-away or sleeping controllers should not leave a frozen pointer.
            if (!OVRInput.GetControllerPositionTracked(state.Controller))
                return false;

            ray = new Ray(state.ControllerAnchor.position, state.ControllerAnchor.forward);
            strength = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, state.Controller);
            return true;
        }

        if (state.Hand == null || !state.Hand.IsTracked ||
            !state.Hand.IsDataValid || !state.Hand.IsPointerPoseValid ||
            state.Hand.IsSystemGestureInProgress || state.Hand.PointerPose == null)
        {
            return false;
        }

        Transform pose = state.Hand.PointerPose;
        ray = new Ray(pose.position, pose.forward);
        strength = state.Hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
        return true;
    }

    private Button FindHoveredButton(Ray ray, out Vector3 hitPosition)
    {
        Button best = null;
        float closest = float.MaxValue;
        hitPosition = default;

        CheckButton(resetButton, ray, ref best, ref closest, ref hitPosition);
        CheckButton(finishButton, ray, ref best, ref closest, ref hitPosition);
        if (sessionFlow != null)
        {
            CheckButton(sessionFlow.RestartButton, ray,
                ref best, ref closest, ref hitPosition);
            CheckButton(sessionFlow.NextButton, ray,
                ref best, ref closest, ref hitPosition);
            CheckButton(sessionFlow.NextButtonPage2, ray,
                ref best, ref closest, ref hitPosition);
            CheckButton(sessionFlow.SkipButtonPage1, ray,
                ref best, ref closest, ref hitPosition);
            CheckButton(sessionFlow.SkipButtonPage2, ray,
                ref best, ref closest, ref hitPosition);
            CheckButton(sessionFlow.UnderstoodButton, ray,
                ref best, ref closest, ref hitPosition);
        }

        if (voiceManager != null)
        {
            foreach (Button muteButton in voiceManager.MuteButtons)
                CheckButton(muteButton, ray, ref best, ref closest, ref hitPosition);
        }

        return best;
    }

    private void CheckButton(Button button, Ray ray, ref Button best,
        ref float closest, ref Vector3 hitPosition)
    {
        if (button == null || !button.isActiveAndEnabled ||
            !button.gameObject.activeInHierarchy || !button.IsInteractable())
        {
            return;
        }

        RectTransform rect = button.GetComponent<RectTransform>();
        if (!TryHitRect(rect, ray, buttonAimPadding, out Vector3 point,
            out float distance) || distance >= closest)
        {
            return;
        }

        best = button;
        closest = distance;
        hitPosition = point;
    }

    private bool TryGetVisibleUiHit(Ray ray, out Vector3 hit, out float distance)
    {
        hit = default;
        distance = float.MaxValue;
        bool found = false;

        if (visibleUiGraphics == null)
            return false;

        foreach (Graphic graphic in visibleUiGraphics)
        {
            if (graphic == null || !graphic.isActiveAndEnabled ||
                !graphic.gameObject.activeInHierarchy || graphic.color.a < 0.06f)
            {
                continue;
            }

            // Ignore camera/video RawImages that may cover a large portion of the view.
            if (!(graphic is Image) && !(graphic is TextMeshProUGUI))
                continue;

            RectTransform rect = graphic.rectTransform;
            if (!TryHitRect(rect, ray, 0f, out Vector3 point, out float t) ||
                t >= distance)
            {
                continue;
            }

            hit = point;
            distance = t;
            found = true;
        }

        return found;
    }

    private bool TryHitRect(RectTransform rect, Ray ray, float padding,
        out Vector3 point, out float distance)
    {
        point = default;
        distance = 0f;
        if (rect == null)
            return false;

        Plane plane = new Plane(rect.forward, rect.position);
        if (!plane.Raycast(ray, out distance) ||
            distance <= 0f || distance > maximumRayDistance)
        {
            return false;
        }

        Vector3 world = ray.GetPoint(distance);
        Vector3 local = rect.InverseTransformPoint(world);
        Rect area = rect.rect;
        area.xMin -= padding;
        area.xMax += padding;
        area.yMin -= padding;
        area.yMax += padding;

        if (!area.Contains(new Vector2(local.x, local.y)))
            return false;

        point = world;
        return true;
    }

    private void CreateResources()
    {
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Unlit/Transparent");

        if (shader != null)
        {
            rayMaterial = new Material(shader);
            rayMaterial.color = Color.white;
        }

        const int size = 128;
        dotTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        dotTexture.filterMode = FilterMode.Bilinear;
        dotTexture.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - size * 0.5f;
                float dy = y + 0.5f - size * 0.5f;
                float radius = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = 1f - Mathf.SmoothStep(60f, 63f, radius);
                dotTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        dotTexture.Apply();
        dotSprite = Sprite.Create(dotTexture, new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f), size);
    }

    private void CreateVisuals(PointerState state, string label)
    {
        GameObject lineObject = new GameObject(label + "Ray");
        lineObject.transform.SetParent(visualRoot.transform, false);

        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.widthMultiplier = rayWidth;
        line.numCapVertices = 8;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        if (rayMaterial != null)
            line.sharedMaterial = rayMaterial;
        line.enabled = false;
        state.Line = line;

        GameObject dotObject = new GameObject(label + "Dot");
        dotObject.transform.SetParent(visualRoot.transform, false);
        SpriteRenderer sprite = dotObject.AddComponent<SpriteRenderer>();
        sprite.sprite = dotSprite;
        sprite.sortingOrder = 30000;
        sprite.color = Color.white;
        sprite.enabled = false;
        state.Dot = sprite;
    }

    private void DrawPointer(PointerState state, Vector3 source,
        Vector3 target, bool activated)
    {
        Vector3 direction = target - source;
        float distance = direction.magnitude;
        if (distance < 0.025f || distance > maximumRayDistance)
        {
            HidePointer(state);
            return;
        }

        direction /= distance;
        float start = Mathf.Min(rayStartOffset, distance * 0.45f);
        float end = Mathf.Max(start + 0.005f, distance - rayEndGap);
        Color tint = activated ? activationTurquoise : Color.white;

        if (state.Line != null)
        {
            state.Line.enabled = true;
            state.Line.widthMultiplier = rayWidth;
            state.Line.SetPosition(0, source + direction * start);
            state.Line.SetPosition(1, source + direction * end);
            Color lineTint = new Color(tint.r, tint.g, tint.b, rayOpacity);
            state.Line.startColor = lineTint;
            state.Line.endColor = lineTint;
        }

        if (state.Dot == null)
            return;

        Transform dot = state.Dot.transform;
        Camera camera = Camera.main;
        Vector3 toViewer = camera != null
            ? camera.transform.position - target
            : -direction;
        if (toViewer.sqrMagnitude < 0.000001f)
            toViewer = -direction;

        dot.position = target + toViewer.normalized * dotSurfaceOffset;
        dot.rotation = Quaternion.LookRotation(toViewer.normalized,
            camera != null ? camera.transform.up : Vector3.up);

        // A world-space sprite cannot disappear behind Canvas sorting or scaling.
        float diameter = Mathf.Clamp(dotSize * 0.0007f, 0.006f, 0.024f);
        dot.localScale = Vector3.one * diameter;
        state.Dot.color = tint;
        state.Dot.enabled = true;
    }

    private void HidePointer(PointerState state)
    {
        if (state.Line != null)
            state.Line.enabled = false;
        if (state.Dot != null)
            state.Dot.enabled = false;
    }

    private void PlayControllerClickHaptic(PointerState state)
    {
        if (!state.IsController || !enableControllerHaptics ||
            clickHapticAmplitude <= 0f)
        {
            return;
        }

        OVRInput.SetControllerVibration(1f, clickHapticAmplitude, state.Controller);
        state.HapticPlaying = true;
        state.HapticStopTime = Time.unscaledTime + clickHapticDurationSeconds;
    }

    private void UpdateControllerHaptic(PointerState state)
    {
        if (state.HapticPlaying && Time.unscaledTime >= state.HapticStopTime)
            StopControllerHaptic(state);
    }

    private void StopControllerHaptic(PointerState state)
    {
        if (!state.IsController || !state.HapticPlaying)
            return;

        OVRInput.SetControllerVibration(0f, 0f, state.Controller);
        state.HapticPlaying = false;
        state.HapticStopTime = -100f;
    }

    private void OnDisable()
    {
        HidePointer(leftHandState);
        HidePointer(rightHandState);
        HidePointer(leftControllerState);
        HidePointer(rightControllerState);
        StopControllerHaptic(leftControllerState);
        StopControllerHaptic(rightControllerState);
    }

    private void OnDestroy()
    {
        if (visualRoot != null)
            Destroy(visualRoot);
        if (rayMaterial != null)
            Destroy(rayMaterial);
        if (dotSprite != null)
            Destroy(dotSprite);
        if (dotTexture != null)
            Destroy(dotTexture);
    }
}
