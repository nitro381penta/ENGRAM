using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

public class EngramHandDiagnostics : MonoBehaviour
{
    [Header("Existing Engram Components")]
    [SerializeField] private EngramMemoryRecall memoryRecall;
    [SerializeField] private EngramSpatialInventory spatialInventory;
    [SerializeField] private OVRHand leftHand;
    [SerializeField] private OVRHand rightHand;

    [Header("XR Hands and Viewer")]
    [SerializeField] private Transform xrTrackingOrigin;
    [SerializeField] private Transform centerEyeAnchor;
    [SerializeField, Range(0.012f, 0.06f)] private float pinchStartDistance = 0.025f;
    [SerializeField, Min(0.02f)] private float nearGrabRadius = 0.12f;

    [Header("Diagnostic Display")]
    [SerializeField] private bool showOverlay = true;
    [SerializeField, Range(0.1f, 0.5f)] private float updateInterval = 0.15f;
    [SerializeField] private bool showTemporaryPinchMarkers = false;

    private readonly List<XRHandSubsystem> handSubsystems = new();
    private XRHandSubsystem xrHands;
    private TextMeshProUGUI overlayText;
    private GameObject overlayObject;
    private GameObject leftMarker;
    private GameObject rightMarker;
    private float nextUpdate;
    private float nextSubsystemSearch;
    private float averageFrameTime;
    private readonly StringBuilder textBuilder = new(1100);

    private void Start()
    {
        if (memoryRecall == null)
            memoryRecall = GetComponent<EngramMemoryRecall>();
        if (spatialInventory == null)
            spatialInventory = FindFirstObjectByType<EngramSpatialInventory>();
        if (centerEyeAnchor == null && Camera.main != null)
            centerEyeAnchor = Camera.main.transform;

        if (showOverlay)
            CreateOverlay();
        if (showTemporaryPinchMarkers)
        {
            leftMarker = CreatePinchMarker("EngramDebugLeftPinch");
            rightMarker = CreatePinchMarker("EngramDebugRightPinch");
        }
    }

    private void Update()
    {
        averageFrameTime = averageFrameTime <= 0f
            ? Time.unscaledDeltaTime
            : Mathf.Lerp(averageFrameTime, Time.unscaledDeltaTime, 0.08f);

        if (Time.unscaledTime < nextUpdate)
            return;
        nextUpdate = Time.unscaledTime + updateInterval;

        RefreshSubsystem();
        textBuilder.Clear();
        textBuilder.AppendLine("ENGRAM | HAND-DIAGNOSE 2");
        textBuilder.Append("Recall: ").Append(memoryRecall != null && memoryRecall.SessionStarted ? "AKTIV" : "INAKTIV");
        textBuilder.Append("   Inventar: ").Append(spatialInventory != null && spatialInventory.ObjectsDocked ? "BEREIT" : "NICHT BEREIT");
        textBuilder.Append("   FPS ~ ").Append((1f / Mathf.Max(0.001f, averageFrameTime)).ToString("F0"));
        textBuilder.AppendLine();

        textBuilder.Append("XR HANDS: ").AppendLine(xrHands != null && xrHands.running ? "RUNNING" : "NICHT GESTARTET");
        textBuilder.Append("XR Tracking Origin: ").AppendLine(xrTrackingOrigin != null ? xrTrackingOrigin.name : "NICHT ZUGEWIESEN");

        if (xrHands != null && xrHands.running)
        {
            AppendXrHand("LINKS", xrHands.leftHand, leftMarker);
            AppendXrHand("RECHTS", xrHands.rightHand, rightMarker);
        }
        else
        {
            if (leftMarker != null) leftMarker.SetActive(false);
            if (rightMarker != null) rightMarker.SetActive(false);
        }

        AppendOvrHand("LINKS", leftHand);
        AppendOvrHand("RECHTS", rightHand);

        if (overlayText != null)
            overlayText.text = textBuilder.ToString();
    }

    private void RefreshSubsystem()
    {
        if (xrHands != null && xrHands.running)
            return;
        if (Time.unscaledTime < nextSubsystemSearch)
            return;
        nextSubsystemSearch = Time.unscaledTime + 1f;
        handSubsystems.Clear();
        SubsystemManager.GetSubsystems(handSubsystems);
        xrHands = null;
        foreach (XRHandSubsystem candidate in handSubsystems)
        {
            if (candidate != null && candidate.running)
            {
                xrHands = candidate;
                break;
            }
        }
    }

    private void AppendXrHand(string side, XRHand hand, GameObject marker)
    {
        textBuilder.AppendLine();
        textBuilder.Append("XR ").Append(side).Append(" | Tracked: ").Append(hand.isTracked ? "JA" : "NEIN");

        Pose thumbPose = default;
        Pose indexPose = default;
        bool hasJoints = hand.isTracked &&
            hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out thumbPose) &&
            hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out indexPose);

        if (!hasJoints)
        {
            textBuilder.AppendLine(" | Gelenkdaten: NEIN");
            if (marker != null) marker.SetActive(false);
            return;
        }

        if (xrTrackingOrigin == null)
        {
            textBuilder.AppendLine(" | Weltkoordinaten: Origin fehlt");
            if (marker != null) marker.SetActive(false);
            return;
        }

        Vector3 thumbWorld = xrTrackingOrigin.TransformPoint(thumbPose.position);
        Vector3 indexWorld = xrTrackingOrigin.TransformPoint(indexPose.position);
        Vector3 pinchWorld = (thumbWorld + indexWorld) * 0.5f;
        float gapCm = Vector3.Distance(thumbWorld, indexWorld) * 100f;
        bool pinchCandidate = gapCm <= pinchStartDistance * 100f;
        float nearestDistance = FindNearestModel(pinchWorld, out string modelName);

        textBuilder.Append(" | Pinch ").Append(pinchCandidate ? "JA" : "NEIN");
        textBuilder.Append(" | Finger: ").Append(gapCm.ToString("F1")).AppendLine(" cm");
        textBuilder.Append("  Naechstes Modell: ").Append(modelName);
        textBuilder.Append(" | Abstand: ").Append(float.IsPositiveInfinity(nearestDistance)
            ? "–" : (nearestDistance * 100f).ToString("F1") + " cm");
        textBuilder.Append(" | Greifradius: ").Append((nearGrabRadius * 100f).ToString("F0")).AppendLine(" cm");
        textBuilder.Append("  Pinch-Welt: ").Append(Format(pinchWorld));
        if (centerEyeAnchor != null)
            textBuilder.Append(" | Abstand zum Kopf: ")
                .Append((Vector3.Distance(centerEyeAnchor.position, pinchWorld) * 100f).ToString("F0"))
                .Append(" cm");
        textBuilder.AppendLine();

        if (marker != null)
        {
            marker.SetActive(true);
            marker.transform.position = pinchWorld;
        }
    }

    private void AppendOvrHand(string side, OVRHand hand)
    {
        textBuilder.Append("OVR ").Append(side).Append(" | ");
        if (hand == null)
        {
            textBuilder.AppendLine("Nicht zugewiesen");
            return;
        }

        bool active = hand.isActiveAndEnabled && hand.gameObject.activeInHierarchy;
        bool tracked = active && hand.IsTracked;
        bool valid = tracked && hand.IsDataValid;
        bool pinch = valid && hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        textBuilder.Append("Tracked: ").Append(tracked ? "JA" : "NEIN");
        textBuilder.Append(" | Daten: ").Append(valid ? "OK" : "NEIN");
        textBuilder.Append(" | Pinch: ").Append(pinch ? "JA" : "NEIN");
        textBuilder.Append(" | Systemgeste: ").Append(valid && hand.IsSystemGestureInProgress ? "JA" : "NEIN");
        textBuilder.AppendLine();
    }

    private float FindNearestModel(Vector3 worldPoint, out string nearestName)
    {
        nearestName = "KEINES";
        if (spatialInventory == null)
            return float.PositiveInfinity;

        float bestDistance = float.PositiveInfinity;
        Transform[] children = spatialInventory.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in children)
        {
            if (!child.name.StartsWith("EngramModel_", StringComparison.Ordinal))
                continue;

            bool found = false;
            Bounds bounds = new(child.position, Vector3.one * 0.04f);
            foreach (Renderer renderer in child.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled)
                    continue;
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }

            float distance = Vector3.Distance(worldPoint, bounds.ClosestPoint(worldPoint));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearestName = child.name.Replace("EngramModel_", "");
            }
        }
        return bestDistance;
    }

    private static string Format(Vector3 v)
    {
        return $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
    }

    private void CreateOverlay()
    {
        if (centerEyeAnchor == null)
        {
            Debug.LogWarning("Engram diagnostics: CenterEyeAnchor fehlt.");
            return;
        }

        overlayObject = new GameObject("EngramHandDebugCanvas", typeof(RectTransform), typeof(Canvas));
        overlayObject.transform.SetParent(centerEyeAnchor, false);
        overlayObject.transform.localPosition = new Vector3(-0.11f, -0.22f, 0.75f);
        overlayObject.transform.localRotation = Quaternion.identity;
        overlayObject.transform.localScale = Vector3.one * 0.001f;

        Canvas canvas = overlayObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 110;
        canvas.worldCamera = centerEyeAnchor.GetComponent<Camera>();
        overlayObject.GetComponent<RectTransform>().sizeDelta = new Vector2(690f, 340f);

        GameObject background = new("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(overlayObject.transform, false);
        RectTransform rect = background.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        Image image = background.GetComponent<Image>();
        image.color = new Color(0.02f, 0.04f, 0.07f, 0.78f);
        image.raycastTarget = false;

        GameObject textObject = new("TrackingStatus", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(background.transform, false);
        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(14f, 8f);
        textRect.offsetMax = new Vector2(-14f, -8f);
        overlayText = textObject.GetComponent<TextMeshProUGUI>();
        overlayText.fontSize = 17f;
        overlayText.enableWordWrapping = false;
        overlayText.color = Color.white;
        overlayText.alignment = TextAlignmentOptions.TopLeft;
        overlayText.raycastTarget = false;
    }

    private static GameObject CreatePinchMarker(string name)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = name;
        marker.transform.localScale = Vector3.one * 0.018f;
        Collider collider = marker.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);
        marker.SetActive(false);
        return marker;
    }

    private void OnDestroy()
    {
        if (overlayObject != null) Destroy(overlayObject);
        if (leftMarker != null) Destroy(leftMarker);
        if (rightMarker != null) Destroy(rightMarker);
    }
}
