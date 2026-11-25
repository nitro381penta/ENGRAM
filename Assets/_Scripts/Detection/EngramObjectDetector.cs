using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using TMPro;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;

public class EngramObjectDetector : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PassthroughCameraAccess cameraAccess;
    [SerializeField] private DetectionOverlay detectionOverlay;
    [SerializeField] private EngramSpatialObjectMarkers spatialMarkers;
    [SerializeField] private TMP_Text detectionStatus;

    [Header("Model")]
    [SerializeField] private ModelAsset modelAsset;
    [SerializeField] private TextAsset classesAsset;
    [SerializeField, Range(0f, 1f)] private float confidenceThreshold = 0.25f;
    [SerializeField, Range(0f, 1f)] private float iouThreshold = 0.5f;

    [Header("Clock Detection (COCO class clock)")]
    [SerializeField, Range(0f, 1f)] private float clockConfidenceThreshold = 0.20f;

    [Header("Cell Phone Detection (COCO class cell phone)")]
    [SerializeField, Range(0f, 1f)] private float cellPhoneConfidenceThreshold = 0.30f;

    [Header("Book Detection (COCO class book)")]
    [SerializeField, Range(0f, 1f)] private float bookConfidenceThreshold = 0.18f;

    [Header("Performance")]
    [SerializeField] private float detectionInterval = 1f;
    [SerializeField] private float readbackTimeout = 15f;

    [Header("Display Stabilization")]
    [SerializeField] private bool stabilizeDetections = true;
    [SerializeField, Range(0.05f, 1f)] private float boxSmoothing = 0.45f;
    [SerializeField] private float detectionHoldTime = 3f;
    [SerializeField] private int minimumDisplayObservations = 2;

    [Header("Debug")]
    [SerializeField] private bool showConfidence = true;
    [SerializeField] private RawImage modelInputPreview;
    [SerializeField] private float statusRefreshInterval = 1.5f;

    private const int InputSize = 640;
    private const int MaxNmsCandidates = 200;
    private const int MaxDetections = 40;

    private Worker worker;
    private Tensor<float> inputTensor;
    private RenderTexture inputTexture;
    private string[] classNames;

    private int resizedWidth;
    private int resizedHeight;
    private int paddingX;
    private int paddingY;

    private string currentStage = "Initializing";
    private string errorStep = "";
    private string errorType = "";
    private string errorMessage = "";

    private int completedRuns;
    private int lastRawCount;
    private int lastCandidateCount;
    private int lastNmsCount;
    private int lastDetectionCount;
    private int lastDisplayedCount;
    private float? lastProcessingMs;
    private float nextStatusRefresh;
    private string lastStatusText = "";

    private readonly HashSet<string> targetClasses = new()
    {
        "cup", "bottle", "bowl", "wine glass", "apple",
        "banana", "book", "cell phone", "clock", "scissors"
    };

    private struct Detection
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
        public float Score;
        public int ClassId;
        public string Label;
    }

    private class TrackedDetection
    {
        public Detection Box;
        public int Observations;
        public float LastSeen;
        public bool SeenThisRun;
    }

    private readonly Dictionary<string, TrackedDetection> trackedDetections = new();
    private bool captureModeEnabled = true;
    private bool initialVisibilityStored;
    private bool originalOverlayVisible;
    private bool originalPreviewVisible;
    private bool originalStatusVisible;

    // The Passthrough camera itself stays active during recall.
    public void SetCaptureMode(bool enabled)
    {
        if (!initialVisibilityStored)
        {
            originalOverlayVisible = detectionOverlay != null &&
                detectionOverlay.gameObject.activeSelf;
            originalPreviewVisible = modelInputPreview != null &&
                modelInputPreview.gameObject.activeSelf;
            originalStatusVisible = detectionStatus != null &&
                detectionStatus.gameObject.activeSelf;
            initialVisibilityStored = true;
        }

        captureModeEnabled = enabled;
        if (!enabled)
        {
            trackedDetections.Clear();
            if (detectionOverlay != null)
                detectionOverlay.Clear();
        }

        // Never deactivate this component or an ancestor of it: the coroutine
        // would stop and a later capture could not resume automatically.
        if (detectionOverlay != null &&
            !transform.IsChildOf(detectionOverlay.transform))
            detectionOverlay.gameObject.SetActive(enabled && originalOverlayVisible);
        if (modelInputPreview != null &&
            !transform.IsChildOf(modelInputPreview.transform))
            modelInputPreview.gameObject.SetActive(enabled && originalPreviewVisible);
        if (detectionStatus != null &&
            !transform.IsChildOf(detectionStatus.transform))
            detectionStatus.gameObject.SetActive(enabled && originalStatusVisible);
    }


    private void Start()
    {
        if (cameraAccess == null || detectionOverlay == null ||
            modelAsset == null || classesAsset == null)
        {
            SetError("References", new InvalidOperationException("Missing detector references."));
            return;
        }

        classNames = classesAsset.text.Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < classNames.Length; i++)
            classNames[i] = classNames[i].Trim();

        try
        {
            SetStage("Loading model");

            Model model = ModelLoader.Load(modelAsset);
            worker = new Worker(model, BackendType.GPUCompute);
            inputTensor = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));

            inputTexture = new RenderTexture(
                InputSize, InputSize, 0, RenderTextureFormat.ARGB32);
            inputTexture.filterMode = FilterMode.Bilinear;
            inputTexture.wrapMode = TextureWrapMode.Clamp;
            inputTexture.Create();

            if (modelInputPreview != null)
                modelInputPreview.texture = inputTexture;
        }
        catch (Exception exception)
        {
            SetError("Initialization", exception);
            return;
        }

        UpdateStatus(true);
        StartCoroutine(DetectionLoop());
    }

    private void Update()
    {
        // Refresh the compact dashboard independently of GPU pipeline stages.
        UpdateStatus();
    }

    private IEnumerator DetectionLoop()
    {
        while (true)
        {
            if (!captureModeEnabled)
            {
                yield return new WaitForSeconds(0.2f);
                continue;
            }

            if (!cameraAccess.IsPlaying)
            {
                trackedDetections.Clear();
                detectionOverlay.Clear();
                SetStage("Waiting for camera");
                yield return null;
                continue;
            }

            Texture cameraFrame = cameraAccess.GetTexture();
            if (cameraFrame == null)
            {
                SetStage("No camera texture");
                yield return null;
                continue;
            }

            Pose imagePose = cameraAccess.GetCameraPose();
            float processingStart = Time.realtimeSinceStartup;
            Tensor<float> outputTensor = null;
            string step = "Preparing input";

            try
            {
                SetStage("Preparing image");
                PrepareLetterbox(cameraFrame);
                TextureConverter.ToTensor(inputTexture, inputTensor, default);

                step = "Worker.Schedule";
                SetStage("Running YOLO11s");
                worker.Schedule(inputTensor);

                step = "PeekOutput";
                outputTensor = worker.PeekOutput(0) as Tensor<float>;
                if (outputTensor == null)
                    throw new InvalidOperationException("YOLO output is not a float tensor.");

                step = "ReadbackRequest";
                SetStage("GPU readback");
                outputTensor.ReadbackRequest();
            }
            catch (Exception exception)
            {
                SetError(step, exception);
                yield break;
            }

            float readbackStart = Time.realtimeSinceStartup;
            while (true)
            {
                bool completed;
                try
                {
                    completed = outputTensor.IsReadbackRequestDone();
                }
                catch (Exception exception)
                {
                    SetError("GPU readback", exception);
                    yield break;
                }

                if (completed)
                    break;

                if (Time.realtimeSinceStartup - readbackStart > readbackTimeout)
                {
                    SetError("GPU readback", new TimeoutException("Readback timeout."));
                    yield break;
                }

                yield return null;
            }

            try
            {
                SetStage("CPU postprocessing");
                using var output = outputTensor.ReadbackAndClone();
                List<Detection> detections = ProcessDetections(output);

                lastDetectionCount = detections.Count;
                UpdateTrackedDetections(detections);
                DrawDetections();

                // Pass the unsmoothed image rectangles to spatial registration.
                // The overlay and YOLO inference pipeline stay unchanged.
                var objectBoxes = new List<Rect>(detections.Count);
                var objectLabels = new List<string>(detections.Count);

                foreach (Detection detection in detections)
                {
                    objectBoxes.Add(new Rect(
                        detection.X - detection.Width * 0.5f,
                        detection.Y - detection.Height * 0.5f,
                        detection.Width,
                        detection.Height));
                    objectLabels.Add(detection.Label);
                }

                lastProcessingMs = (Time.realtimeSinceStartup - processingStart) * 1000f;
                completedRuns++;

                if (spatialMarkers != null)
                    spatialMarkers.UpdateDetectionsWithBoxes(
                        objectBoxes, objectLabels, imagePose);

                SetStage("Completed");
            }
            catch (Exception exception)
            {
                SetError("Postprocessing / Spatial", exception);
                yield break;
            }

            yield return new WaitForSeconds(Mathf.Max(0.05f, detectionInterval));
        }
    }

    private void PrepareLetterbox(Texture cameraFrame)
    {
        int imageWidth = cameraFrame.width;
        int imageHeight = cameraFrame.height;
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new InvalidOperationException("Invalid camera texture resolution.");

        float scale = Mathf.Min(
            (float)InputSize / imageWidth, (float)InputSize / imageHeight);

        resizedWidth = Mathf.Clamp(Mathf.RoundToInt(imageWidth * scale), 1, InputSize);
        resizedHeight = Mathf.Clamp(Mathf.RoundToInt(imageHeight * scale), 1, InputSize);
        paddingX = (InputSize - resizedWidth) / 2;
        paddingY = (InputSize - resizedHeight) / 2;

        RenderTexture previous = RenderTexture.active;
        try
        {
            RenderTexture.active = inputTexture;
            GL.PushMatrix();
            try
            {
                GL.LoadPixelMatrix(0, InputSize, InputSize, 0);
                float gray = 114f / 255f;
                GL.Clear(true, true, new Color(gray, gray, gray, 1f));
                Graphics.DrawTexture(
                    new Rect(paddingX, paddingY, resizedWidth, resizedHeight), cameraFrame);
            }
            finally
            {
                GL.PopMatrix();
            }
        }
        finally
        {
            RenderTexture.active = previous;
        }
    }

    private List<Detection> ProcessDetections(Tensor<float> output)
    {
        if (output.shape.rank != 3 || output.shape[0] != 1)
            throw new InvalidOperationException($"Unexpected YOLO shape: {output.shape}");

        int channelCount = output.shape[1];
        int predictionCount = output.shape[2];
        int classCount = channelCount - 4;

        if (classCount != classNames.Length)
            throw new InvalidOperationException(
                $"Model has {classCount} classes, but classes.txt contains {classNames.Length}.");

        lastRawCount = predictionCount;
        var candidates = new List<Detection>();

        for (int i = 0; i < predictionCount; i++)
        {
            // Evaluate the class first; apply its own confidence threshold next.
            float bestScore = 0f;
            int bestClass = -1;

            for (int c = 0; c < classCount; c++)
            {
                float score = output[0, c + 4, i];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestClass < 0)
                continue;

            string label = classNames[bestClass];
            if (!targetClasses.Contains(label))
                continue;

            float requiredScore = confidenceThreshold;
            if (label == "clock")
                requiredScore = Mathf.Min(requiredScore, clockConfidenceThreshold);
            else if (label == "cell phone")
                requiredScore = Mathf.Min(requiredScore, cellPhoneConfidenceThreshold);
            else if (label == "book")
                requiredScore = Mathf.Min(requiredScore, bookConfidenceThreshold);

            if (bestScore < requiredScore)
                continue;

            float x = output[0, 0, i];
            float y = output[0, 1, i];
            float width = output[0, 2, i];
            float height = output[0, 3, i];

            if (width <= 0f || height <= 0f)
                continue;

            if (!TryConvertBoundingBox(
                x, y, width, height,
                out float convertedX, out float convertedY,
                out float convertedWidth, out float convertedHeight))
                continue;

            candidates.Add(new Detection
            {
                X = convertedX,
                Y = convertedY,
                Width = convertedWidth,
                Height = convertedHeight,
                Score = bestScore,
                ClassId = bestClass,
                Label = label
            });
        }

        lastCandidateCount = candidates.Count;
        List<Detection> nmsResults = ApplyNms(candidates);
        lastNmsCount = nmsResults.Count;
        return SelectBestPerClass(nmsResults);
    }

    private bool TryConvertBoundingBox(
        float centerX, float centerY, float width, float height,
        out float convertedX, out float convertedY,
        out float convertedWidth, out float convertedHeight)
    {
        convertedX = 0f;
        convertedY = 0f;
        convertedWidth = 0f;
        convertedHeight = 0f;

        float left = centerX - width * 0.5f;
        float right = centerX + width * 0.5f;
        float top = centerY - height * 0.5f;
        float bottom = centerY + height * 0.5f;

        left = (left - paddingX) / resizedWidth;
        right = (right - paddingX) / resizedWidth;
        top = (top - paddingY) / resizedHeight;
        bottom = (bottom - paddingY) / resizedHeight;

        left = Mathf.Clamp01(left);
        right = Mathf.Clamp01(right);
        top = Mathf.Clamp01(top);
        bottom = Mathf.Clamp01(bottom);

        if (right <= left || bottom <= top)
            return false;

        convertedX = (left + right) * 0.5f * InputSize;
        convertedY = (top + bottom) * 0.5f * InputSize;
        convertedWidth = (right - left) * InputSize;
        convertedHeight = (bottom - top) * InputSize;
        return true;
    }

    private List<Detection> ApplyNms(List<Detection> candidates)
    {
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        int count = Mathf.Min(candidates.Count, MaxNmsCandidates);
        var selected = new List<Detection>();

        for (int i = 0; i < count; i++)
        {
            Detection candidate = candidates[i];
            bool suppressed = false;

            foreach (Detection previous in selected)
            {
                if (candidate.ClassId != previous.ClassId)
                    continue;
                if (CalculateIoU(candidate, previous) > iouThreshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (suppressed)
                continue;

            selected.Add(candidate);
            if (selected.Count >= MaxDetections)
                break;
        }

        return selected;
    }

    private List<Detection> SelectBestPerClass(List<Detection> detections)
    {
        var bestByClass = new Dictionary<int, Detection>();
        foreach (Detection detection in detections)
        {
            if (!bestByClass.TryGetValue(detection.ClassId, out Detection previous) ||
                detection.Score > previous.Score)
                bestByClass[detection.ClassId] = detection;
        }

        var result = new List<Detection>(bestByClass.Values);
        result.Sort((a, b) => b.Score.CompareTo(a.Score));
        return result;
    }

    private float CalculateIoU(Detection a, Detection b)
    {
        float aLeft = a.X - a.Width * 0.5f;
        float aRight = a.X + a.Width * 0.5f;
        float aTop = a.Y - a.Height * 0.5f;
        float aBottom = a.Y + a.Height * 0.5f;
        float bLeft = b.X - b.Width * 0.5f;
        float bRight = b.X + b.Width * 0.5f;
        float bTop = b.Y - b.Height * 0.5f;
        float bBottom = b.Y + b.Height * 0.5f;

        float intersectionWidth = Mathf.Max(
            0f, Mathf.Min(aRight, bRight) - Mathf.Max(aLeft, bLeft));
        float intersectionHeight = Mathf.Max(
            0f, Mathf.Min(aBottom, bBottom) - Mathf.Max(aTop, bTop));

        float intersection = intersectionWidth * intersectionHeight;
        float union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0f ? intersection / union : 0f;
    }

    private void UpdateTrackedDetections(List<Detection> detections)
    {
        float now = Time.unscaledTime;
        foreach (TrackedDetection tracked in trackedDetections.Values)
            tracked.SeenThisRun = false;

        foreach (Detection detection in detections)
        {
            if (!trackedDetections.TryGetValue(detection.Label, out TrackedDetection tracked))
            {
                trackedDetections[detection.Label] = new TrackedDetection
                {
                    Box = detection,
                    Observations = 1,
                    LastSeen = now,
                    SeenThisRun = true
                };
                continue;
            }

            float elapsed = now - tracked.LastSeen;
            bool sameRegion = CalculateIoU(tracked.Box, detection) >= 0.1f;
            if (elapsed > detectionHoldTime)
                tracked.Observations = 0;

            // Smooth only the on-screen overlay; 3D uses the current detections.
            Detection updatedBox = detection;
            if (stabilizeDetections && sameRegion && elapsed <= detectionHoldTime)
            {
                Detection previous = tracked.Box;
                updatedBox.X = Mathf.Lerp(previous.X, detection.X, boxSmoothing);
                updatedBox.Y = Mathf.Lerp(previous.Y, detection.Y, boxSmoothing);
                updatedBox.Width = Mathf.Lerp(previous.Width, detection.Width, boxSmoothing);
                updatedBox.Height = Mathf.Lerp(previous.Height, detection.Height, boxSmoothing);
            }

            tracked.Box = updatedBox;
            tracked.Observations++;
            tracked.LastSeen = now;
            tracked.SeenThisRun = true;
        }

        var expired = new List<string>();
        foreach (var pair in trackedDetections)
        {
            if (!stabilizeDetections || now - pair.Value.LastSeen > detectionHoldTime)
            {
                if (!pair.Value.SeenThisRun)
                    expired.Add(pair.Key);
            }
        }

        foreach (string label in expired)
            trackedDetections.Remove(label);
    }

    private void DrawDetections()
    {
        detectionOverlay.Clear();
        int displayed = 0;

        foreach (TrackedDetection tracked in trackedDetections.Values)
        {
            if (stabilizeDetections &&
                tracked.Observations < minimumDisplayObservations)
                continue;
            if (!stabilizeDetections && !tracked.SeenThisRun)
                continue;

            Detection detection = tracked.Box;
            string label = detection.Label;
            if (showConfidence)
                label += $" {detection.Score:F2}";
            if (!tracked.SeenThisRun)
                label += " (held)";

            detectionOverlay.AddBox(
                detection.X, detection.Y,
                detection.Width, detection.Height, label);
            displayed++;
        }

        lastDisplayedCount = displayed;
    }

    private void SetStage(string stage)
    {
        currentStage = stage;
        // Do not update the visible UI for GPU/readback/CPU transitions.
    }

    private void SetError(string step, Exception exception)
    {
        currentStage = "ERROR";
        errorStep = step;
        errorType = exception.GetType().Name;
        errorMessage = exception.Message;
        Debug.LogError($"Engram error at {step}:\n{exception}");
        UpdateStatus(true);
    }

    private void UpdateStatus(bool force = false)
    {
        if (detectionStatus == null)
            return;
        if (!force && Time.unscaledTime < nextStatusRefresh)
            return;

        nextStatusRefresh = Time.unscaledTime + Mathf.Max(0.5f, statusRefreshInterval);

        bool cameraActive = cameraAccess != null && cameraAccess.IsPlaying;
        string cameraState = cameraActive ? "ACTIVE" : "WAITING";
        string modelState = worker != null ? "READY" : "LOADING";
        string overallState = currentStage == "ERROR"
            ? "ERROR"
            : !cameraActive
                ? "WAITING"
                : completedRuns > 0 ? "COMPLETE" : "STARTING";

        // The GPU pipeline continues independently of this status display.
        string status =
            $"CAMERA: {cameraState}\n" +
            $"YOLO: {modelState}\n" +
            $"STATUS: {overallState}\n" +
            $"OBJECTS: {lastDetectionCount}\n" +
            $"PROCESSING: {(lastProcessingMs.HasValue ? lastProcessingMs.Value.ToString("F0") + " MS" : "--")}";

        if (status == lastStatusText)
            return;

        lastStatusText = status;
        detectionStatus.text = status;
    }

    private void OnDestroy()
    {
        worker?.Dispose();
        inputTensor?.Dispose();

        if (inputTexture != null)
        {
            inputTexture.Release();
            Destroy(inputTexture);
        }
    }
}