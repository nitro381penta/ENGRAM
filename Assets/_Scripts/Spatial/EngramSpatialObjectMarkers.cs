using System;
using System.Collections.Generic;
using System.IO;
using Meta.XR;
using TMPro;
using UnityEngine;

public class EngramSpatialObjectMarkers : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PassthroughCameraAccess cameraAccess;
    [SerializeField] private EngramTableReference tableReference;
    [SerializeField] private Material markerMaterial;
    [SerializeField] private TMP_Text objectStatus;
    [SerializeField] private EnvironmentRaycastManager environmentRaycastManager;

    [Header("Spatial Registration")]
    [SerializeField] private float markerSize = 0.04f;
    [SerializeField] private float markerSurfaceOffset = 0.005f;
    [SerializeField] private bool flipImageY = true;
    [SerializeField] private float maximumTableRadius = 1.25f;

    [Header("Geometry v2: Camera Views")]
    [SerializeField] private float observationLifetime = 25f;
    [SerializeField] private int maximumRayObservations = 22;
    [SerializeField] private float minimumViewBaseline = 0.09f;
    [SerializeField] private float minimumViewAngle = 8f;
    [SerializeField] private float maximumTriangulationError = 0.055f;
    [SerializeField] private float maximumTriangulationOffset = 0.22f;
    [SerializeField, Range(0f, 1f)] private float triangulationWeight = 0.8f;

    [Header("Geometry v2: Sparse Depth")]
    [SerializeField] private bool useEnvironmentDepth = true;
    [SerializeField, Range(1, 6)] private int depthSamplesPerObject = 3;
    [SerializeField, Range(0, 40)] private int maximumDepthQueriesPerPass = 18;
    [SerializeField] private int maximumSurfaceObservations = 100;
    [SerializeField] private float minimumSurfaceHeight = 0.006f;
    [SerializeField] private float maximumObjectHeight = 0.5f;
    [SerializeField] private float maximumSurfaceOffset = 0.30f;
    [SerializeField] private float maximumRayDistance = 2.5f;
    [SerializeField] private float flatObjectHeight = 0.045f;
    [SerializeField] private float tallObjectHeight = 0.14f;
    [SerializeField, Range(0f, 1f)] private float depthFootprintWeight = 0.25f;
    [SerializeField] private bool logGeometryDiagnostics = false;

    [Header("Cup Placement (only COCO class cup)")]
    [SerializeField, Range(0f, 1f)] private float cupBaseProjectionWeight = 1f;
    [SerializeField, Range(0f, 1f)] private float cupTriangulationWeight = 0.15f;
    [SerializeField, Range(0f, 1f)] private float cupDepthFootprintWeight = 0f;

    [Header("Wine Glass Placement (only COCO class wine glass)")]
    [SerializeField, Range(0.90f, 1f)] private float wineGlassBasePixelFraction = 0.97f;
    [SerializeField, Min(3)] private int wineGlassMinimumConsistentSamples = 5;
    [SerializeField] private float wineGlassClusterRadius = 0.08f;
    [SerializeField] private float wineGlassMaximumMedianDeviation = 0.035f;
    [SerializeField] private float wineGlassMarkerDeadZone = 0.006f;

    [Header("Banana Footprint Center (only COCO class banana)")]
    [SerializeField] private bool bananaUseFootprintCenter = true;
    [SerializeField, Min(4)] private int bananaMinimumDepthSamples = 8;
    [SerializeField, Min(0.01f)] private float bananaMinimumFootprintSpan = 0.07f;
    [SerializeField, Min(0.10f)] private float bananaMaximumFootprintSpan = 0.35f;
    [SerializeField, Min(0.01f)] private float bananaMaximumCorrection = 0.22f;
    [SerializeField, Min(0f)] private float bananaMinimumSurfaceHeight = 0.012f;
    [SerializeField, Min(0.02f)] private float bananaMaximumSurfaceHeight = 0.12f;
    [SerializeField, Min(0f)] private float bananaMinimumTimeSpan = 0.75f;
    [SerializeField, Min(1f)] private float bananaObservationAge = 12f;
    [SerializeField, Min(0.01f)] private float bananaMinimumViewBaseline = 0.06f;
    [SerializeField] private bool bananaRequireMeasuredFootprintForCapture = false;

    [Header("Geometry v2: Preview")]
    [SerializeField] private bool freezeAfterMultiViewEstimate = true;
    [SerializeField] private int minimumLockObservations = 6;
    [SerializeField] private float maximumLockDeviation = 0.045f;

    [Header("Position Estimation")]
    [SerializeField] private int minimumConsistentSamples = 4;
    [SerializeField] private int maximumSamplesPerObject = 12;

    [SerializeField] private float sampleLifetime = 6f;
    [SerializeField] private float clusterRadius = 0.15f;
    [SerializeField] private float maximumMedianDeviation = 0.07f;

    [SerializeField, Range(0.05f, 1f)]
    private float markerSmoothing = 0.2f;

    [SerializeField] private float maximumMarkerStep = 0.025f;
    [SerializeField] private float markerMovementDeadZone = 0.02f;
    [SerializeField, Min(0f)] private float minimumMarkerSpacing = 0.05f;

    [Header("Tracking")]
    [SerializeField] private float candidateExpirationTime = 4f;
    [SerializeField] private float recentObservationTime = 2.5f;

    [SerializeField] private float maximumPoseRotation = 6f;
    [SerializeField] private float maximumPoseTranslation = 0.05f;

    [Header("Capture")]
    [SerializeField] private float statusRefreshInterval = 1.5f;

    private const float InputSize = 640f;

    private readonly Dictionary<string, Candidate> candidates = new();
    private readonly List<GameObject> markers = new();

    // Fired only after successfully writing the capture file.
    public event Action<int> PositionsSaved;

    private bool positionsCaptured;
    private int savedObjectCount;
    private int nextMarkerOrder;
    private float nextStatusRefresh;
    private float noticeExpiresAt;
    private string notice = "";
    private string lastStatusText = "";

    private struct PositionSample
    {
        public Vector3 Position;
        public float Timestamp;
    }

    private struct RayObservation
    {
        public Vector3 Origin;
        public Vector3 Direction;
        public float Timestamp;
    }

    private struct SurfaceObservation
    {
        public Vector3 LocalPosition;
        public Vector3 CameraLocalPosition;
        public float Timestamp;
    }

    private class Candidate
    {
        public string Label;
        public readonly List<PositionSample> Samples = new();
        public readonly List<RayObservation> Rays = new();
        public readonly List<SurfaceObservation> Surfaces = new();

        public Vector3 EstimatedPosition;
        public Vector3 DisplayPosition;
        public float LastDetectionTime;
        public float LastStableObservation;
        public float EstimatedHeight = -1f;
        public int ConsistentSamples;
        public int ConsecutiveStableFrames;
        public float MedianDeviation;
        public bool IsStable;
        public bool HasDisplayPosition;
        public bool PreviewFrozen;
        public bool HasTriangulation;
        public bool HasDepthFootprint;
        public bool HasBananaFootprintCenter;
        public Vector3 BananaFootprintCenterLocal;
        public GameObject Marker;
        public int MarkerOrder;
    }

    private static readonly Vector2[] DepthSampleFractions =
    {
        new Vector2(0.50f, 0.50f),
        new Vector2(0.28f, 0.30f),
        new Vector2(0.72f, 0.30f),
        new Vector2(0.28f, 0.70f),
        new Vector2(0.72f, 0.70f),
        new Vector2(0.50f, 0.20f),
        new Vector2(0.50f, 0.80f),
        new Vector2(0.20f, 0.50f),
        new Vector2(0.80f, 0.50f)
    };

    private int depthSampleFrame;

    [Serializable]
    private class ObjectRecord
    {
        public string label;
        public Vector3 localPosition;
    }

    [Serializable]
    private class ObjectData
    {
        public string anchorUuid;
        public List<ObjectRecord> objects = new();
    }

    private string SavePath => Path.Combine(
        Application.persistentDataPath,
        "EngramSpatialObjects.json"
    );

    private void Awake()
    {
        if (environmentRaycastManager == null)
            environmentRaycastManager = GetComponent<EnvironmentRaycastManager>();
    }

    private void OnEnable()
    {
        if (tableReference == null)
            return;

        tableReference.ReferenceReady += OnReferenceReady;
        tableReference.ReferenceCleared += OnReferenceCleared;
    }

    private void Start()
    {
        if (cameraAccess == null ||
            tableReference == null ||
            markerMaterial == null)
        {
            SetStatus("Missing references");
            enabled = false;
            return;
        }

        if (tableReference.HasReference)
        {
            OnReferenceReady();
        }
        else
        {
            SetStatus("Waiting for table reference");
        }
    }

    private void Update()
    {
        if (OVRInput.GetDown(
            OVRInput.Button.One,
            OVRInput.Controller.LTouch))
        {
            CapturePositions();
        }

        if (OVRInput.GetDown(
            OVRInput.Button.Two,
            OVRInput.Controller.LTouch))
        {
            ClearSavedPositions();
        }

        if (!positionsCaptured)
        {
            RemoveExpiredCandidates();
            UpdateMarkerVisibility();
        }

        RefreshStatus();
    }

    // Existing callers remain supported; Geometry v2 uses the full rectangles.
    public void UpdateDetections(
        IReadOnlyList<Vector2> contactPoints,
        IReadOnlyList<string> labels,
        Pose imagePose)
    {
        UpdateLegacyDetections(contactPoints, null, labels, imagePose);
    }

    public void UpdateDetections(
        IReadOnlyList<Vector2> contactPoints,
        IReadOnlyList<Vector2> centerPoints,
        IReadOnlyList<string> labels,
        Pose imagePose)
    {
        UpdateLegacyDetections(contactPoints, centerPoints, labels, imagePose);
    }

    private void UpdateLegacyDetections(
        IReadOnlyList<Vector2> contactPoints,
        IReadOnlyList<Vector2> centerPoints,
        IReadOnlyList<string> labels,
        Pose imagePose)
    {
        if (!CanProcess(imagePose) || contactPoints == null || labels == null ||
            contactPoints.Count != labels.Count)
            return;

        RemoveExpiredCandidates();
        for (int i = 0; i < labels.Count; i++)
        {
            Vector2 pixel = contactPoints[i];
            Ray ray = GetImageRay(pixel, imagePose);
            if (!tableReference.TryProjectRay(ray, out Vector3 point))
                continue;
            if (!IsInsideTableArea(point))
                continue;

            Candidate candidate = GetOrCreateCandidate(labels[i]);
            if (candidate == null)
                continue;

            candidate.LastDetectionTime = Time.unscaledTime;
            AddCenterRay(candidate,
                centerPoints != null && centerPoints.Count == labels.Count
                    ? centerPoints[i] : contactPoints[i], imagePose);

            UpdateCandidate(candidate, point);
        }
        UpdateMarkerVisibility();
        RefreshStatus();
    }

    // Rect uses image pixels 0..640, with Y increasing downwards.
    public void UpdateDetectionsWithBoxes(
        IReadOnlyList<Rect> imageBoxes,
        IReadOnlyList<string> labels,
        Pose imagePose)
    {
        if (!CanProcess(imagePose) || imageBoxes == null || labels == null ||
            imageBoxes.Count != labels.Count)
            return;

        RemoveExpiredCandidates();
        depthSampleFrame++;
        int remainingDepthQueries = Mathf.Max(0, maximumDepthQueriesPerPass);

        for (int i = 0; i < imageBoxes.Count; i++)
        {
            Rect box = imageBoxes[i];
            if (box.width <= 1f || box.height <= 1f)
                continue;

            bool isWineGlass = IsWineGlass(labels[i]);
            Vector3 centerOnTable;
            Vector3 baseOnTable;
            Vector2 glassBasePixel = default;

            if (isWineGlass)
            {
                // Transparent glass: project the lower bounding-box point to the table.
                glassBasePixel = new Vector2(box.center.x,
                    box.yMin + box.height * wineGlassBasePixelFraction);
                if (!tableReference.TryProjectRay(
                    GetImageRay(glassBasePixel, imagePose), out baseOnTable) ||
                    !IsInsideTableArea(baseOnTable))
                    continue;
                centerOnTable = baseOnTable;
            }
            else
            {
                if (!TryGetPlanarEstimates(box, imagePose,
                    out centerOnTable, out baseOnTable))
                    continue;
                if (!IsInsideTableArea(centerOnTable) &&
                    !IsInsideTableArea(baseOnTable))
                    continue;
            }

            Candidate candidate = GetOrCreateCandidate(labels[i]);
            if (candidate == null)
                continue;

            candidate.LastDetectionTime = Time.unscaledTime;
            if (candidate.PreviewFrozen)
            {
                candidate.IsStable = true;
                candidate.LastStableObservation = Time.unscaledTime;
                continue;
            }

            if (isWineGlass)
            {
                // Avoid unreliable depth and triangulation through transparent glass.
                AddCenterRay(candidate, glassBasePixel, imagePose);
                candidate.HasTriangulation = false;
                candidate.HasDepthFootprint = false;
                if (logGeometryDiagnostics)
                    Debug.Log($"Engram wine glass: base projection " +
                        $"({baseOnTable.x:F3}, {baseOnTable.z:F3})");
                UpdateCandidate(candidate, baseOnTable);
                continue;
            }

            AddCenterRay(candidate, box.center, imagePose);
            var heights = new List<float>();
            int depthHits = SampleDepth(
                candidate, box, imagePose, centerOnTable, baseOnTable,
                ref remainingDepthQueries, heights);

            // Use depth samples from several images, not a single noisy hit.
            if (candidate.Surfaces.Count >= 3)
            {
                var recentHeights = new List<float>();
                Transform anchor = tableReference.AnchorTransform;
                int first = Mathf.Max(0, candidate.Surfaces.Count - 15);
                for (int j = first; j < candidate.Surfaces.Count; j++)
                {
                    Vector3 point = anchor.TransformPoint(
                        candidate.Surfaces[j].LocalPosition);
                    recentHeights.Add(point.y - anchor.position.y);
                }

                float measuredHeight = Median(recentHeights);
                candidate.EstimatedHeight = candidate.EstimatedHeight < 0f
                    ? measuredHeight
                    : Mathf.Lerp(candidate.EstimatedHeight, measuredHeight, 0.35f);
            }

            // Thin objects are centered by their image footprint; tall objects
            // use the projected bottom as the initial ground-plane estimate.
            bool flatByClass = candidate.Label == "book" ||
                               candidate.Label == "cell phone";
            float tallFraction = candidate.EstimatedHeight >= 0f
                ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                    flatObjectHeight, tallObjectHeight,
                    candidate.EstimatedHeight))
                : (flatByClass ? 0.05f : 0.9f);

            // A cup's sparse depth hits may underestimate its height,
            // making the image-center projection land behind the cup.
            // For this class alone, favor the projected contact area.
            bool isCup = string.Equals(candidate.Label, "cup",
                StringComparison.OrdinalIgnoreCase);
            if (isCup)
                tallFraction = cupBaseProjectionWeight;

            Vector3 estimate = Vector3.Lerp(
                centerOnTable, baseOnTable, tallFraction);

            candidate.HasTriangulation = false;
            if (TryTriangulate(candidate, out Vector3 localCenter,
                out float reprojectionError))
            {
                Vector3 triangulatedWorld = tableReference.AnchorTransform
                    .TransformPoint(localCenter);
                triangulatedWorld.y = tableReference.AnchorTransform.position.y;

                if (HorizontalDistance(triangulatedWorld, estimate) <=
                    maximumTriangulationOffset &&
                    IsInsideTableArea(triangulatedWorld))
                {
                    float rayWeight = isCup
                        ? cupTriangulationWeight : triangulationWeight;
                    estimate = Vector3.Lerp(
                        estimate, triangulatedWorld, rayWeight);
                    candidate.HasTriangulation = true;
                }

                if (logGeometryDiagnostics)
                    Debug.Log($"Engram {candidate.Label}: ray error " +
                        $"{reprojectionError * 100f:F1} cm");
            }

            candidate.HasDepthFootprint = false;
            if (TryEstimateDepthFootprint(candidate,
                out Vector3 footprintLocal))
            {
                Vector3 footprintWorld = tableReference.AnchorTransform
                    .TransformPoint(footprintLocal);
                footprintWorld.y = tableReference.AnchorTransform.position.y;

                if (HorizontalDistance(footprintWorld, estimate) <=
                    maximumTriangulationOffset &&
                    IsInsideTableArea(footprintWorld))
                {
                    float depthWeight = isCup
                        ? cupDepthFootprintWeight
                        : candidate.HasTriangulation
                            ? depthFootprintWeight
                            : depthFootprintWeight * 0.5f;
                    estimate = Vector3.Lerp(
                        estimate, footprintWorld, depthWeight);
                    candidate.HasDepthFootprint = true;
                }
            }

            // For curved bananas, use the center of the observed footprint,
            // rather than a single surface point on one side of the fruit.
            bool isBanana = bananaUseFootprintCenter &&
                string.Equals(candidate.Label, "banana",
                    StringComparison.OrdinalIgnoreCase);
            if (isBanana)
            {
                if (TryEstimateBananaFootprintCenter(candidate, estimate,
                    out Vector3 bananaCenterWorld))
                {
                    estimate = bananaCenterWorld;
                    Vector3 centerLocal = tableReference.AnchorTransform
                        .InverseTransformPoint(bananaCenterWorld);

                    if (!candidate.HasBananaFootprintCenter)
                    {
                        Debug.Log("Engram: Banana footprint center acquired.");
                        // Replace earlier estimates once a footprint is validated.
                        candidate.Samples.Clear();
                        candidate.ConsecutiveStableFrames = 0;
                        candidate.DisplayPosition = centerLocal;
                        candidate.HasDisplayPosition = true;
                    }

                    candidate.BananaFootprintCenterLocal = centerLocal;
                    candidate.HasBananaFootprintCenter = true;
                }
                else if (candidate.HasBananaFootprintCenter)
                {
                    // Short depth gaps must not move the estimate back to
                    // an unrelated point on the banana's visible surface.
                    estimate = tableReference.AnchorTransform.TransformPoint(
                        candidate.BananaFootprintCenterLocal);
                }
            }

            if (logGeometryDiagnostics)
                Debug.Log($"Engram {candidate.Label}: depth hits " +
                    $"{depthHits}, triangulated={candidate.HasTriangulation}, " +
                    $"footprint={candidate.HasDepthFootprint}" +
                    (isBanana
                        ? $", bananaFootprint={candidate.HasBananaFootprintCenter}"
                        : ""));

            UpdateCandidate(candidate, estimate);
        }

        UpdateMarkerVisibility();
        RefreshStatus();
    }

    private bool CanProcess(Pose imagePose)
    {
        if (!isActiveAndEnabled || positionsCaptured ||
            cameraAccess == null || tableReference == null ||
            !tableReference.HasReference || !cameraAccess.IsPlaying)
            return false;

        Pose now = cameraAccess.GetCameraPose();
        return Quaternion.Angle(imagePose.rotation, now.rotation) <=
            maximumPoseRotation &&
            Vector3.Distance(imagePose.position, now.position) <=
            maximumPoseTranslation;
    }

    private Candidate GetOrCreateCandidate(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;
        if (candidates.TryGetValue(label, out Candidate candidate))
            return candidate;

        candidate = new Candidate
        {
            Label = label,
            LastStableObservation = -1000f
        };
        candidates.Add(label, candidate);
        return candidate;
    }

    private bool IsInsideTableArea(Vector3 point)
    {
        Vector3 origin = tableReference.AnchorTransform.position;
        return HorizontalDistance(point, origin) <= maximumTableRadius;
    }

    private Vector2 ToViewport(Vector2 pixel)
    {
        float x = Mathf.Clamp01(pixel.x / InputSize);
        float y = Mathf.Clamp01(pixel.y / InputSize);
        return new Vector2(x, flipImageY ? 1f - y : y);
    }

    private Ray GetImageRay(Vector2 pixel, Pose pose)
    {
        return cameraAccess.ViewportPointToRay(ToViewport(pixel), pose);
    }

    private bool TryGetPlanarEstimates(Rect box, Pose imagePose,
        out Vector3 center, out Vector3 bottom)
    {
        center = default;
        bottom = default;

        if (!tableReference.TryProjectRay(
            GetImageRay(box.center, imagePose), out center))
            return false;

        Vector2 basePixel = new Vector2(
            box.center.x, box.yMin + box.height * 0.95f);
        if (!tableReference.TryProjectRay(
            GetImageRay(basePixel, imagePose), out bottom))
            return false;

        // A small perspective correction improves flat rectangular objects.
        Vector3 sum = Vector3.zero;
        int count = 0;
        for (int iy = 0; iy < 2; iy++)
        {
            for (int ix = 0; ix < 2; ix++)
            {
                Vector2 pixel = new Vector2(
                    box.xMin + box.width * (ix == 0 ? 0.22f : 0.78f),
                    box.yMin + box.height * (iy == 0 ? 0.22f : 0.78f));

                if (!tableReference.TryProjectRay(
                    GetImageRay(pixel, imagePose), out Vector3 point))
                    continue;
                if (HorizontalDistance(point, center) > 0.45f)
                    continue;
                sum += point;
                count++;
            }
        }

        if (count >= 3)
        {
            Vector3 footprintCenter = sum / count;
            footprintCenter.y = center.y;
            center = Vector3.Lerp(center, footprintCenter, 0.5f);
        }

        return true;
    }

    private void AddCenterRay(Candidate candidate, Vector2 pixel, Pose pose)
    {
        Ray ray = GetImageRay(pixel, pose);
        Transform anchor = tableReference.AnchorTransform;
        candidate.Rays.Add(new RayObservation
        {
            Origin = anchor.InverseTransformPoint(ray.origin),
            Direction = anchor.InverseTransformDirection(ray.direction).normalized,
            Timestamp = Time.unscaledTime
        });
        TrimObservations(candidate);
    }

    private int SampleDepth(
        Candidate candidate, Rect box, Pose pose,
        Vector3 planeCenter, Vector3 planeBottom,
        ref int remainingQueries, List<float> measuredHeights)
    {
        if (!useEnvironmentDepth || environmentRaycastManager == null ||
            !environmentRaycastManager.enabled || remainingQueries <= 0)
            return 0;

        Transform anchor = tableReference.AnchorTransform;
        int hits = 0;
        int requested = Mathf.Min(depthSamplesPerObject, remainingQueries);
        for (int k = 0; k < requested; k++)
        {
            // Rotate the sparse sampling pattern across detection passes.
            int index = (depthSampleFrame * 3 + k) %
                DepthSampleFractions.Length;
            Vector2 fraction = DepthSampleFractions[index];
            Vector2 pixel = new Vector2(
                box.xMin + box.width * fraction.x,
                box.yMin + box.height * fraction.y);

            remainingQueries--;
            Ray ray = GetImageRay(pixel, pose);
            if (!environmentRaycastManager.Raycast(
                ray, out EnvironmentRaycastHit hit, maximumRayDistance))
                continue;

            float height = hit.point.y - anchor.position.y;
            if (height < minimumSurfaceHeight ||
                height > maximumObjectHeight)
                continue;

            float distance = Mathf.Min(
                HorizontalDistance(hit.point, planeCenter),
                HorizontalDistance(hit.point, planeBottom));
            if (distance > maximumSurfaceOffset ||
                !IsInsideTableArea(hit.point))
                continue;

            candidate.Surfaces.Add(new SurfaceObservation
            {
                LocalPosition = anchor.InverseTransformPoint(hit.point),
                CameraLocalPosition = anchor.InverseTransformPoint(ray.origin),
                Timestamp = Time.unscaledTime
            });
            measuredHeights.Add(height);
            hits++;
        }

        TrimObservations(candidate);
        return hits;
    }

    private void TrimObservations(Candidate candidate)
    {
        float oldest = Time.unscaledTime -
            Mathf.Max(4f, observationLifetime);
        candidate.Rays.RemoveAll(value => value.Timestamp < oldest);
        candidate.Surfaces.RemoveAll(value => value.Timestamp < oldest);

        while (candidate.Rays.Count > Mathf.Max(4, maximumRayObservations))
            candidate.Rays.RemoveAt(0);
        while (candidate.Surfaces.Count > Mathf.Max(8, maximumSurfaceObservations))
            candidate.Surfaces.RemoveAt(0);
    }

    private bool HasDiverseViews(List<RayObservation> rays)
    {
        for (int i = 0; i < rays.Count; i++)
        {
            for (int j = i + 1; j < rays.Count; j++)
            {
                if (Vector3.Distance(rays[i].Origin, rays[j].Origin) >=
                    minimumViewBaseline &&
                    Vector3.Angle(rays[i].Direction, rays[j].Direction) >=
                    minimumViewAngle)
                    return true;
            }
        }
        return false;
    }

    private bool TryTriangulate(Candidate candidate,
        out Vector3 center, out float medianError)
    {
        center = default;
        medianError = float.PositiveInfinity;
        List<RayObservation> rays = candidate.Rays;
        if (rays.Count < 4 || !HasDiverseViews(rays))
            return false;

        if (!SolveLeastSquares(rays, out Vector3 rough))
            return false;

        var inliers = new List<RayObservation>();
        for (int i = 0; i < rays.Count; i++)
        {
            RayObservation ray = rays[i];
            float distance = DistancePointToRay(
                rough, ray.Origin, ray.Direction);
            if (distance <= maximumTriangulationError * 1.8f)
                inliers.Add(ray);
        }

        if (inliers.Count < 4 ||
            inliers.Count < Mathf.CeilToInt(rays.Count * 0.65f) ||
            !HasDiverseViews(inliers) ||
            !SolveLeastSquares(inliers, out center))
            return false;

        var errors = new List<float>(inliers.Count);
        foreach (RayObservation ray in inliers)
        {
            if (Vector3.Dot(center - ray.Origin, ray.Direction) <= 0f)
                return false;
            errors.Add(DistancePointToRay(
                center, ray.Origin, ray.Direction));
        }
        medianError = Median(errors);
        if (medianError > maximumTriangulationError)
            return false;

        Vector3 world = tableReference.AnchorTransform.TransformPoint(center);
        float relativeHeight = world.y -
            tableReference.AnchorTransform.position.y;
        return relativeHeight >= -0.04f &&
               relativeHeight <= maximumObjectHeight;
    }

    private bool SolveLeastSquares(List<RayObservation> rays,
        out Vector3 point)
    {
        // Solve Sum((I-dd^T)p) = Sum((I-dd^T)o).
        float a00 = 0f, a01 = 0f, a02 = 0f;
        float a11 = 0f, a12 = 0f, a22 = 0f;
        Vector3 b = Vector3.zero;

        foreach (RayObservation ray in rays)
        {
            Vector3 d = ray.Direction;
            float m00 = 1f - d.x * d.x;
            float m01 = -d.x * d.y;
            float m02 = -d.x * d.z;
            float m11 = 1f - d.y * d.y;
            float m12 = -d.y * d.z;
            float m22 = 1f - d.z * d.z;

            a00 += m00; a01 += m01; a02 += m02;
            a11 += m11; a12 += m12; a22 += m22;
            Vector3 o = ray.Origin;
            b += new Vector3(
                m00 * o.x + m01 * o.y + m02 * o.z,
                m01 * o.x + m11 * o.y + m12 * o.z,
                m02 * o.x + m12 * o.y + m22 * o.z);
        }

        // Invert the symmetric normal matrix; skip ill-conditioned geometry.
        float det = a00 * (a11 * a22 - a12 * a12) -
                    a01 * (a01 * a22 - a02 * a12) +
                    a02 * (a01 * a12 - a02 * a11);
        if (Mathf.Abs(det) < 0.0001f)
        {
            point = default;
            return false;
        }

        float inv00 = (a11 * a22 - a12 * a12) / det;
        float inv01 = (a02 * a12 - a01 * a22) / det;
        float inv02 = (a01 * a12 - a02 * a11) / det;
        float inv11 = (a00 * a22 - a02 * a02) / det;
        float inv12 = (a01 * a02 - a00 * a12) / det;
        float inv22 = (a00 * a11 - a01 * a01) / det;

        point = new Vector3(
            inv00 * b.x + inv01 * b.y + inv02 * b.z,
            inv01 * b.x + inv11 * b.y + inv12 * b.z,
            inv02 * b.x + inv12 * b.y + inv22 * b.z);
        return IsFinite(point.x) && IsFinite(point.y) &&
               IsFinite(point.z);
    }

    private bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private float DistancePointToRay(Vector3 point,
        Vector3 origin, Vector3 direction)
    {
        return Vector3.Cross(point - origin, direction).magnitude;
    }

    private bool TryEstimateDepthFootprint(Candidate candidate,
        out Vector3 footprintLocal)
    {
        footprintLocal = default;
        var samples = candidate.Surfaces;
        if (samples.Count < 9)
            return false;

        // Do not use a one-sided point cloud as an object centroid.
        bool diverse = false;
        for (int i = 0; i < samples.Count && !diverse; i++)
        {
            for (int j = i + 1; j < samples.Count; j++)
            {
                if (Vector3.Distance(
                    samples[i].CameraLocalPosition,
                    samples[j].CameraLocalPosition) >= minimumViewBaseline)
                {
                    diverse = true;
                    break;
                }
            }
        }
        if (!diverse)
            return false;

        var xs = new List<float>(samples.Count);
        var zs = new List<float>(samples.Count);
        foreach (SurfaceObservation sample in samples)
        {
            xs.Add(sample.LocalPosition.x);
            zs.Add(sample.LocalPosition.z);
        }
        xs.Sort();
        zs.Sort();

        float x0 = Percentile(xs, 0.15f);
        float x1 = Percentile(xs, 0.85f);
        float z0 = Percentile(zs, 0.15f);
        float z1 = Percentile(zs, 0.85f);
        if (x1 - x0 > 0.65f || z1 - z0 > 0.65f)
            return false;

        footprintLocal = new Vector3((x0 + x1) * 0.5f,
            0f, (z0 + z1) * 0.5f);
        return true;
    }

    // Estimate the center of the banana's ground-plane extent. The center
    // may lie in the empty space inside the curve; it need not be a depth hit.
    private bool TryEstimateBananaFootprintCenter(Candidate candidate,
        Vector3 estimatedWorld, out Vector3 centerWorld)
    {
        centerWorld = default;
        if (!useEnvironmentDepth ||
            candidate.Surfaces.Count < bananaMinimumDepthSamples)
            return false;

        Transform anchor = tableReference.AnchorTransform;
        float now = Time.unscaledTime;
        var accepted = new List<SurfaceObservation>();

        foreach (SurfaceObservation sample in candidate.Surfaces)
        {
            if (now - sample.Timestamp > bananaObservationAge)
                continue;

            Vector3 world = anchor.TransformPoint(sample.LocalPosition);
            float height = world.y - anchor.position.y;
            if (height < bananaMinimumSurfaceHeight ||
                height > bananaMaximumSurfaceHeight ||
                HorizontalDistance(world, estimatedWorld) >
                    bananaMaximumCorrection)
                continue;

            accepted.Add(sample);
        }

        if (accepted.Count < bananaMinimumDepthSamples)
            return false;

        float earliest = float.PositiveInfinity;
        float latest = float.NegativeInfinity;
        foreach (SurfaceObservation sample in accepted)
        {
            earliest = Mathf.Min(earliest, sample.Timestamp);
            latest = Mathf.Max(latest, sample.Timestamp);
        }
        if (latest - earliest < bananaMinimumTimeSpan)
            return false;

        bool diverseViews = false;
        for (int i = 0; i < accepted.Count && !diverseViews; i++)
        {
            for (int j = i + 1; j < accepted.Count; j++)
            {
                if (Vector3.Distance(
                    accepted[i].CameraLocalPosition,
                    accepted[j].CameraLocalPosition) >=
                    bananaMinimumViewBaseline)
                {
                    diverseViews = true;
                    break;
                }
            }
        }
        if (!diverseViews)
            return false;

        var xs = new List<float>(accepted.Count);
        var zs = new List<float>(accepted.Count);
        foreach (SurfaceObservation sample in accepted)
        {
            xs.Add(sample.LocalPosition.x);
            zs.Add(sample.LocalPosition.z);
        }
        xs.Sort();
        zs.Sort();

        // Trim the extremes to reduce the effect of sporadic depth outliers.
        float x0 = Percentile(xs, 0.10f);
        float x1 = Percentile(xs, 0.90f);
        float z0 = Percentile(zs, 0.10f);
        float z1 = Percentile(zs, 0.90f);
        float spanX = x1 - x0;
        float spanZ = z1 - z0;

        if (Mathf.Max(spanX, spanZ) < bananaMinimumFootprintSpan ||
            Mathf.Max(spanX, spanZ) > bananaMaximumFootprintSpan)
            return false;

        Vector3 centerLocal = new Vector3(
            (x0 + x1) * 0.5f, 0f, (z0 + z1) * 0.5f);
        centerWorld = anchor.TransformPoint(centerLocal);
        centerWorld.y = anchor.position.y;

        if (!IsInsideTableArea(centerWorld) ||
            HorizontalDistance(centerWorld, estimatedWorld) >
                bananaMaximumCorrection)
            return false;

        return true;
    }

    private float Percentile(List<float> sorted, float fraction)
    {
        if (sorted.Count == 0)
            return 0f;
        float index = Mathf.Clamp01(fraction) * (sorted.Count - 1);
        int lower = Mathf.FloorToInt(index);
        int upper = Mathf.CeilToInt(index);
        return Mathf.Lerp(sorted[lower], sorted[upper], index - lower);
    }

    private void UpdateCandidate(Candidate candidate, Vector3 worldPoint)
    {
        if (candidate.PreviewFrozen)
        {
            candidate.IsStable = true;
            candidate.LastStableObservation = Time.unscaledTime;
            return;
        }

        float now = Time.unscaledTime;
        Vector3 localPosition = tableReference.AnchorTransform
            .InverseTransformPoint(worldPoint);
        candidate.Samples.Add(new PositionSample
        {
            Position = localPosition,
            Timestamp = now
        });

        RemoveOldSamples(candidate);
        EvaluateCandidate(candidate);
        if (!candidate.IsStable)
            return;

        if (candidate.Marker == null)
        {
            candidate.Marker = CreateMarker(
                candidate.Label, candidate.EstimatedPosition);
            candidate.MarkerOrder = ++nextMarkerOrder;
        }

        Vector3 target = candidate.EstimatedPosition;
        if (!candidate.HasDisplayPosition)
        {
            candidate.DisplayPosition = target;
            candidate.HasDisplayPosition = true;
        }
        else if (Vector3.Distance(
            candidate.DisplayPosition, target) >
            (IsWineGlass(candidate.Label)
                ? wineGlassMarkerDeadZone : markerMovementDeadZone))
        {
            Vector3 lerped = Vector3.Lerp(
                candidate.DisplayPosition, target, markerSmoothing);
            candidate.DisplayPosition = Vector3.MoveTowards(
                candidate.DisplayPosition, lerped, maximumMarkerStep);
        }

        float offset = markerSize * 0.5f + markerSurfaceOffset;
        candidate.Marker.transform.localPosition =
            candidate.DisplayPosition +
            tableReference.AnchorTransform.InverseTransformVector(
                Vector3.up * offset);

        // For transparent glass, validate stable base projections across views.
        bool wineGlassValidated = IsWineGlass(candidate.Label) &&
            candidate.Rays.Count >= minimumLockObservations &&
            HasDiverseViews(candidate.Rays);
        if (freezeAfterMultiViewEstimate &&
            (candidate.HasTriangulation || wineGlassValidated) &&
            (!bananaUseFootprintCenter ||
             !string.Equals(candidate.Label, "banana",
                 StringComparison.OrdinalIgnoreCase) ||
             candidate.HasBananaFootprintCenter) &&
            candidate.ConsecutiveStableFrames >= minimumLockObservations &&
            candidate.MedianDeviation <= maximumLockDeviation)
        {
            candidate.PreviewFrozen = true;
            if (wineGlassValidated)
            {
                // Match the saved contact point and the frozen marker position.
                candidate.DisplayPosition = candidate.EstimatedPosition;
                candidate.Marker.transform.localPosition =
                    candidate.DisplayPosition +
                    tableReference.AnchorTransform.InverseTransformVector(
                        Vector3.up * offset);
            }
        }
    }

    private static bool IsWineGlass(string label)
    {
        return string.Equals(label, "wine glass",
            StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveOldSamples(Candidate candidate)
    {
        float cutoff = Time.unscaledTime - sampleLifetime;
        candidate.Samples.RemoveAll(sample => sample.Timestamp < cutoff);
        int maximum = Mathf.Max(minimumConsistentSamples, maximumSamplesPerObject);
        while (candidate.Samples.Count > maximum)
            candidate.Samples.RemoveAt(0);
        TrimObservations(candidate);
    }

    private void EvaluateCandidate(Candidate candidate)
    {
        candidate.IsStable = false;
        candidate.ConsistentSamples = 0;
        List<PositionSample> samples = candidate.Samples;
        bool isWineGlass = IsWineGlass(candidate.Label);
        int requiredSamples = isWineGlass
            ? Mathf.Max(minimumConsistentSamples, wineGlassMinimumConsistentSamples)
            : minimumConsistentSamples;
        float allowedClusterRadius = isWineGlass
            ? Mathf.Min(clusterRadius, wineGlassClusterRadius)
            : clusterRadius;
        float allowedMedianDeviation = isWineGlass
            ? Mathf.Min(maximumMedianDeviation, wineGlassMaximumMedianDeviation)
            : maximumMedianDeviation;
        if (samples.Count < requiredSamples)
            return;

        int bestSeed = -1;
        int bestCount = 0;
        float bestError = float.MaxValue;
        for (int i = 0; i < samples.Count; i++)
        {
            Vector3 reference = samples[i].Position;
            int count = 0;
            float totalError = 0f;
            foreach (PositionSample sample in samples)
            {
                float distance = HorizontalDistance(reference, sample.Position);
                if (distance > allowedClusterRadius)
                    continue;
                count++;
                totalError += distance;
            }
            if (count > bestCount ||
                (count == bestCount && totalError < bestError))
            {
                bestCount = count;
                bestSeed = i;
                bestError = totalError;
            }
        }
        if (bestSeed < 0)
            return;

        Vector3 seed = samples[bestSeed].Position;
        var cluster = new List<PositionSample>();
        foreach (PositionSample sample in samples)
            if (HorizontalDistance(seed, sample.Position) <= allowedClusterRadius)
                cluster.Add(sample);

        candidate.ConsistentSamples = cluster.Count;
        var xs = new List<float>(cluster.Count);
        var zs = new List<float>(cluster.Count);
        foreach (PositionSample sample in cluster)
        {
            xs.Add(sample.Position.x);
            zs.Add(sample.Position.z);
        }

        Vector3 preliminaryLocal = new Vector3(
            Median(xs), 0f, Median(zs));
        Transform anchor = tableReference.AnchorTransform;
        Vector3 estimateWorld = anchor.TransformPoint(preliminaryLocal);
        estimateWorld.y = anchor.position.y;
        Vector3 estimate = anchor.InverseTransformPoint(estimateWorld);

        var deviations = new List<float>(cluster.Count);
        foreach (PositionSample sample in cluster)
            deviations.Add(HorizontalDistance(estimate, sample.Position));
        candidate.MedianDeviation = Median(deviations);
        candidate.EstimatedPosition = estimate;

        float inlierFraction = (float)cluster.Count / samples.Count;
        bool latestFits = HorizontalDistance(
            estimate, samples[samples.Count - 1].Position) <= allowedClusterRadius;
        candidate.IsStable = cluster.Count >= requiredSamples &&
            candidate.MedianDeviation <= allowedMedianDeviation &&
            inlierFraction >= 0.6f && latestFits;

        if (candidate.IsStable)
        {
            candidate.LastStableObservation = Time.unscaledTime;
            candidate.ConsecutiveStableFrames++;
        }
        else
        {
            candidate.ConsecutiveStableFrames = 0;
        }
    }

    private void RemoveExpiredCandidates()
    {
        if (candidates.Count == 0)
            return;

        float now = Time.unscaledTime;

        var expired = new List<string>();

        foreach (var pair in candidates)
        {
            Candidate candidate = pair.Value;

            RemoveOldSamples(candidate);

            if (now - candidate.LastDetectionTime >
                candidateExpirationTime)
            {
                expired.Add(pair.Key);
            }
        }

        foreach (string label in expired)
        {
            Candidate candidate = candidates[label];

            if (candidate.Marker != null)
            {
                markers.Remove(candidate.Marker);
                Destroy(candidate.Marker);
            }

            candidates.Remove(label);
        }
    }

    private void UpdateMarkerVisibility()
    {
        float now = Time.unscaledTime;
        var eligible = new List<Candidate>();

        foreach (Candidate candidate in candidates.Values)
        {
            if (candidate.Marker == null)
                continue;

            bool recentlyDetected =
                now - candidate.LastDetectionTime <= candidateExpirationTime;

            if (recentlyDetected)
                eligible.Add(candidate);
            else if (candidate.Marker.activeSelf)
                candidate.Marker.SetActive(false);
        }

        // Prefer the marker that was registered first to avoid switching.
        eligible.Sort((a, b) => a.MarkerOrder.CompareTo(b.MarkerOrder));

        var shownPositions = new List<Vector3>();

        foreach (Candidate candidate in eligible)
        {
            Vector3 position = candidate.DisplayPosition;

            bool show = !IsNearAny(position, shownPositions);

            if (candidate.Marker.activeSelf != show)
                candidate.Marker.SetActive(show);

            if (show)
                shownPositions.Add(position);
        }
    }

    private bool IsNearAny(Vector3 position, List<Vector3> positions)
    {
        foreach (Vector3 other in positions)
        {
            if (HorizontalDistance(position, other) < minimumMarkerSpacing)
                return true;
        }

        return false;
    }

    private float HorizontalDistance(
        Vector3 a,
        Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;

        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private float Median(List<float> values)
    {
        if (values.Count == 0)
            return 0f;

        values.Sort();

        int middle = values.Count / 2;

        if (values.Count % 2 == 1)
            return values[middle];

        return (
            values[middle - 1] +
            values[middle]
        ) * 0.5f;
    }

    private int CountStableCandidates()
    {
        int count = 0;
        float now = Time.unscaledTime;

        foreach (Candidate candidate in candidates.Values)
        {
            bool recent =
                now - candidate.LastStableObservation <=
                recentObservationTime;

            if (candidate.IsStable && recent &&
                candidate.Marker != null && candidate.Marker.activeSelf)
                count++;
        }

        return count;
    }

    private string GetPendingSummary()
    {
        var pending = new List<string>();

        foreach (Candidate candidate in candidates.Values)
        {
            bool recent =
                Time.unscaledTime -
                candidate.LastStableObservation <=
                recentObservationTime;

            if (candidate.IsStable && recent)
                continue;

            pending.Add(
                $"{candidate.Label} " +
                $"{candidate.ConsistentSamples}/" +
                $"{minimumConsistentSamples}"
            );

            if (pending.Count >= 3)
                break;
        }

        return string.Join(", ", pending);
    }

    public void CapturePositions()
    {
        if (!tableReference.HasReference)
        {
            SetStatus("Table reference not ready");
            return;
        }

        if (positionsCaptured)
        {
            SetStatus("Positions already saved");
            return;
        }

        RemoveExpiredCandidates();

        // Save every distinct, currently visible and stable object.
        var ordered = new List<Candidate>(candidates.Values);
        ordered.Sort((a, b) => a.MarkerOrder.CompareTo(b.MarkerOrder));

        var stableCandidates = new List<Candidate>();
        var selectedPositions = new List<Vector3>();

        foreach (Candidate candidate in ordered)
        {
            bool recent =
                Time.unscaledTime - candidate.LastStableObservation <=
                recentObservationTime;

            if (!candidate.IsStable || !recent ||
                candidate.Marker == null || !candidate.Marker.activeSelf)
                continue;

            if (bananaUseFootprintCenter &&
                bananaRequireMeasuredFootprintForCapture &&
                string.Equals(candidate.Label, "banana",
                    StringComparison.OrdinalIgnoreCase) &&
                !candidate.HasBananaFootprintCenter)
                continue;

            Vector3 position = candidate.HasDisplayPosition
                ? candidate.DisplayPosition
                : candidate.EstimatedPosition;

            if (IsNearAny(position, selectedPositions))
                continue;

            stableCandidates.Add(candidate);
            selectedPositions.Add(position);
        }

        if (stableCandidates.Count == 0)
        {
            SetStatus("No stable objects to save");
            return;
        }

        ObjectData data = new ObjectData
        {
            anchorUuid = tableReference.AnchorUuid.ToString()
        };

        foreach (Candidate candidate in stableCandidates)
        {
            if (bananaUseFootprintCenter &&
                string.Equals(candidate.Label, "banana",
                    StringComparison.OrdinalIgnoreCase) &&
                !candidate.HasBananaFootprintCenter)
            {
                Debug.LogWarning(
                    "Engram: Banana saved with fallback position: " +
                    "not enough valid depth observations for footprint center.");
            }

            data.objects.Add(new ObjectRecord
            {
                label = candidate.Label,
                localPosition = candidate.HasDisplayPosition
                    ? candidate.DisplayPosition
                    : candidate.EstimatedPosition
            });
        }

        try
        {
            string json = JsonUtility.ToJson(data, true);

            File.WriteAllText(SavePath, json);

            ClearMarkers();

            foreach (ObjectRecord record in data.objects)
            {
                GameObject marker = CreateMarker(
                    record.label,
                    record.localPosition
                );

                marker.SetActive(true);
            }

            // ClearMarkers() resets the capture state, so set it AFTER rebuilding.
            savedObjectCount = data.objects.Count;
            positionsCaptured = true;
            SetStatus($"Saved {data.objects.Count} object positions");
            PositionsSaved?.Invoke(data.objects.Count);

            Debug.Log(
                $"Engram: Object positions saved to {SavePath}"
            );
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"Engram: Could not save positions: {exception}"
            );

            SetStatus("Position save failed");
        }
    }

    private void OnReferenceReady()
    {
        ClearMarkers();

        if (!File.Exists(SavePath))
        {
            SetStatus("Table ready | Detect objects");
            return;
        }

        try
        {
            string json = File.ReadAllText(SavePath);

            ObjectData data =
                JsonUtility.FromJson<ObjectData>(json);

            if (data == null ||
                data.objects == null ||
                data.anchorUuid !=
                tableReference.AnchorUuid.ToString())
            {
                SetStatus("No saved objects for this table");
                return;
            }

            var restoredPositions = new List<Vector3>();

            foreach (ObjectRecord record in data.objects)
            {
                if (string.IsNullOrWhiteSpace(record.label) ||
                    IsNearAny(record.localPosition, restoredPositions))
                    continue;

                GameObject marker = CreateMarker(
                    record.label,
                    record.localPosition
                );

                marker.SetActive(true);
                restoredPositions.Add(record.localPosition);
            }

            savedObjectCount = restoredPositions.Count;
            positionsCaptured = savedObjectCount > 0;
            SetStatus($"Restored {savedObjectCount} object positions");
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"Engram: Could not restore positions: {exception}"
            );

            SetStatus("Position restoration failed");
        }
    }

    public void ClearSavedPositions()
    {
        try
        {
            if (File.Exists(SavePath))
                File.Delete(SavePath);

            ClearMarkers();

            SetStatus("Object positions cleared");
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"Engram: Could not clear positions: {exception}"
            );

            SetStatus("Position deletion failed");
        }
    }

    private void OnReferenceCleared()
    {
        ClearSavedPositions();
        SetStatus("Waiting for table reference");
    }

    private void ClearMarkers()
    {
        foreach (GameObject marker in markers)
        {
            if (marker != null)
                Destroy(marker);
        }

        markers.Clear();
        candidates.Clear();
        nextMarkerOrder = 0;

        positionsCaptured = false;
        savedObjectCount = 0;
    }

    private GameObject CreateMarker(
        string label,
        Vector3 localPosition)
    {
        GameObject marker =
            GameObject.CreatePrimitive(PrimitiveType.Sphere);

        marker.name = $"Engram_{label}";

        marker.transform.SetParent(
            tableReference.AnchorTransform,
            false
        );

        float offset =
            markerSize * 0.5f +
            markerSurfaceOffset;

        marker.transform.localPosition =
            localPosition +
            tableReference.AnchorTransform.InverseTransformVector(
                Vector3.up * offset);

        marker.transform.localScale =
            Vector3.one * markerSize;

        marker.GetComponent<Renderer>()
            .sharedMaterial = markerMaterial;

        Collider collider = marker.GetComponent<Collider>();

        if (collider != null)
            Destroy(collider);

        marker.SetActive(false);

        markers.Add(marker);

        return marker;
    }

    private void SetStatus(string status)
    {
        if (status.StartsWith("Saved "))
            notice = "SAVED";
        else if (status.StartsWith("Restored "))
            notice = "RESTORED";
        else if (status == "Object positions cleared")
            notice = "CLEARED";
        else if (status.StartsWith("No stable"))
            notice = "NO OBJECTS";
        else if (status.Contains("failed") || status.Contains("Missing"))
            notice = "ERROR";
        else if (status.Contains("Waiting for table"))
            notice = "WAIT TABLE";
        else
            notice = "SCANNING";

        noticeExpiresAt = Time.unscaledTime + 2.5f;
        RefreshStatus(true);
    }

    private void RefreshStatus(bool force = false)
    {
        if (objectStatus == null)
            return;
        if (!force && Time.unscaledTime < nextStatusRefresh)
            return;

        nextStatusRefresh = Time.unscaledTime +
            Mathf.Max(0.5f, statusRefreshInterval);

        bool tableReady = tableReference != null && tableReference.HasReference;
        int count = positionsCaptured ? savedObjectCount : CountStableCandidates();
        string state = !tableReady ? "WAIT TABLE" :
            positionsCaptured ? "SAVED" : "SCANNING";

        if (Time.unscaledTime < noticeExpiresAt)
            state = notice;

        // Always three short lines, regardless of the number of candidates.
        string text =
            $"OBJECTS: {count}\n" +
            $"STATE: {state}\n" +
            "X SAVE  Y RESET";

        if (text == lastStatusText)
            return;

        lastStatusText = text;
        objectStatus.text = text;
    }

    private void OnDisable()
    {
        if (tableReference != null)
        {
            tableReference.ReferenceReady -= OnReferenceReady;
            tableReference.ReferenceCleared -= OnReferenceCleared;
        }

        foreach (GameObject marker in markers)
        {
            if (marker != null)
                marker.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        ClearMarkers();
    }
}