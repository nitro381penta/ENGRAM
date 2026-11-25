using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;

public class EngramMemoryRecall : MonoBehaviour
{
    [Serializable]
    public class ObjectResult
    {
        public string cocoLabel;
        public Vector3 referenceLocalPosition;
        public Vector3 recalledLocalPosition;
        public float horizontalErrorCm;
        public int placementCount;
        public int correctionCount;
    }

    [Serializable]
    public class RecallResult
    {
        public int schemaVersion = 1;
        public string timestampUtc;
        public string anchorUuid;

        public int objectCount;
        public float durationSeconds;
        public int totalCorrections;
        public float meanHorizontalErrorCm;

        public List<ObjectResult> objects = new();
    }

    private sealed class RecallItem
    {
        public EngramSpatialInventory.RecallObject Source;

        public bool Placed;
        public bool IsHandHeld;
        public bool WasPlacedBeforeGrab;

        public int PlacementCount;
        public int CorrectionCount;

        public Vector3 RecalledLocalPosition;
        public Vector3 PreviousLocalPosition;

        public Vector3 DockWorldPosition;
        public Quaternion DockWorldRotation;
        public Vector3 DockLocalScale;
    }

    private sealed class HandState
    {
        public OVRHand Hand;
        public OVRSkeleton Skeleton;

        public bool PinchActive;
        public RecallItem HeldItem;

        public Vector3 GrabOffset;
        public float LastTrackedTime;
    }

    private enum ControllerSide
    {
        None,
        Left,
        Right
    }

    [Header("Existing Engram Components")]
    [SerializeField] private EngramTableReference tableReference;
    [SerializeField] private EngramSpatialInventory spatialInventory;

    [Header("OVR Hand Tracking")]
    [SerializeField] private bool enableHandTracking = true;
    [SerializeField] private OVRHand leftHand;
    [SerializeField] private OVRHand rightHand;
    [SerializeField] private OVRSkeleton leftSkeleton;
    [SerializeField] private OVRSkeleton rightSkeleton;

    [Header("Pinch Gesture")]
    [SerializeField, Range(0f, 1f)]
    private float pinchStartThreshold = 0.65f;

    [SerializeField, Range(0f, 1f)]
    private float pinchReleaseThreshold = 0.35f;

    [SerializeField, Min(0.02f)]
    private float handGrabRadius = 0.12f;

    [SerializeField, Min(0f)]
    private float trackingLossGracePeriod = 0.25f;

    [Header("Existing Controller Ray Grab")]
    [SerializeField] private bool enableControllers = true;
    [SerializeField] private Transform leftControllerAnchor;
    [SerializeField] private Transform rightControllerAnchor;

    [SerializeField, Range(0.1f, 1f)]
    private float triggerThreshold = 0.6f;

    [SerializeField] private bool allowGripTrigger = true;

    [SerializeField, Min(0.3f)]
    private float maximumRayDistance = 3f;

    [SerializeField, Min(0f)]
    private float controllerAimTolerance = 0.075f;

    [SerializeField, Min(0.02f)]
    private float controllerNearGrabRadius = 0.12f;

    [Header("Table Placement")]
    [SerializeField, Min(0.1f)]
    private float maximumPlacementRadius = 0.7f;

    [SerializeField]
    private Vector2 tableCenterOffsetLocal = Vector2.zero;

    [SerializeField, Min(0f)]
    private float minimumDistanceFromInventory = 0.23f;

    [SerializeField, Min(0f)]
    private float previewLift = 0.015f;

    [Header("Task")]
    [SerializeField] private bool autoStartWhenDocked = true;
    [SerializeField] private TMP_Text optionalStatusText;

    [Header("Results")]
    [SerializeField] private bool saveResultAsJson = true;

    private readonly List<RecallItem> items = new();
    private readonly List<EngramSpatialInventory.RecallObject> sourceBuffer = new();

    private readonly HandState leftState = new();
    private readonly HandState rightState = new();

    private RecallItem controllerHeldItem;
    private ControllerSide controllerSide;

    private bool controllerPreviewValid;
    private Vector3 controllerPreviewLocal;

    private float controllerGrabDistance;
    private Vector3 controllerGrabOffset;

    private bool sessionStarted;
    private bool completed;

    private double recallStartTime;
    private int totalCorrections;
    private string lastStatus = "";

    public bool SessionStarted => sessionStarted;
    public bool Completed => completed;
    public int PlacedCount { get; private set; }
    public int ObjectCount => items.Count;
    public int TotalCorrections => totalCorrections;

    public float ElapsedSeconds
    {
        get
        {
            if (!sessionStarted)
                return 0f;

            if (completed && LastResult != null)
                return LastResult.durationSeconds;

            return Mathf.Max(
                0f,
                (float)(Time.realtimeSinceStartupAsDouble - recallStartTime)
            );
        }
    }

    public bool CanFinishRecall =>
        sessionStarted &&
        !completed &&
        items.Count == 4 &&
        PlacedCount == items.Count &&
        controllerHeldItem == null &&
        leftState.HeldItem == null &&
        rightState.HeldItem == null &&
        tableReference != null &&
        tableReference.HasReference;

    public RecallResult LastResult { get; private set; }
    public string LastSavedResultPath { get; private set; }
    public bool ResultSavedSuccessfully { get; private set; }
    public string LastSaveError { get; private set; }

    public event Action RecallStarted;
    public event Action RecallReset;
    public event Action<int, int> RecallProgressChanged;
    public event Action<string, bool> ObjectPlaced;
    public event Action<string> ObjectGrabbed;
    public event Action<string> StatusChanged;
    public event Action<RecallResult> RecallCompleted;

    private void Start()
    {
        if (tableReference == null || spatialInventory == null)
        {
            Debug.LogError(
                "Engram Recall: Table Reference or Spatial Inventory missing."
            );

            enabled = false;
            return;
        }

        leftState.Hand = leftHand;
        leftState.Skeleton = leftSkeleton;

        rightState.Hand = rightHand;
        rightState.Skeleton = rightSkeleton;
    }

    private void Update()
    {
        if (tableReference == null ||
            spatialInventory == null ||
            !tableReference.HasReference ||
            !spatialInventory.ObjectsDocked)
        {
            if (sessionStarted)
                ClearSession();

            return;
        }

        if (!sessionStarted && autoStartWhenDocked)
            StartRecall();

        if (!sessionStarted || completed)
            return;

        if (enableHandTracking)
        {
            UpdateHand(leftState);
            UpdateHand(rightState);
        }

        if (enableControllers)
            UpdateControllers();
    }

    [ContextMenu("Engram/Start Recall")]
    public void StartRecall()
    {
        if (sessionStarted ||
            tableReference == null ||
            spatialInventory == null ||
            !tableReference.HasReference ||
            !spatialInventory.TryBeginRecall(sourceBuffer))
        {
            return;
        }

        items.Clear();

        foreach (EngramSpatialInventory.RecallObject source in sourceBuffer)
        {
            if (source.Model == null)
            {
                Debug.LogError(
                    "Engram Recall: An inventory model is missing."
                );

                ClearSession();
                return;
            }

            RecallItem item = new RecallItem
            {
                Source = source,
                DockWorldPosition = source.DockWorldPosition,
                DockWorldRotation = source.DockWorldRotation,
                DockLocalScale = source.DockLocalScale
            };

            items.Add(item);
        }

        ResetHandState(leftState);
        ResetHandState(rightState);

        controllerHeldItem = null;
        controllerSide = ControllerSide.None;
        controllerPreviewValid = false;
        controllerGrabDistance = 0f;
        controllerGrabOffset = Vector3.zero;

        sessionStarted = true;
        completed = false;

        PlacedCount = 0;
        totalCorrections = 0;

        LastResult = null;
        LastSavedResultPath = null;
        LastSaveError = null;
        ResultSavedSuccessfully = false;

        recallStartTime = Time.realtimeSinceStartupAsDouble;

        SetStatus(
            "Recall ready: place all four objects on the table."
        );

        RecallStarted?.Invoke();
        NotifyProgress();
    }

    public string GetObjectLabel(int index)
    {
        if (index < 0 || index >= items.Count)
            return "";

        return items[index].Source.CocoLabel;
    }

    // Read-only targeting information for the UI/scene pointer.
    // The existing grab, pinch and placement logic is unchanged.
    public bool IsControllerHoldingObject(OVRInput.Controller controller)
    {
        return controllerHeldItem != null &&
               ((controller == OVRInput.Controller.LTouch &&
                 controllerSide == ControllerSide.Left) ||
                (controller == OVRInput.Controller.RTouch &&
                 controllerSide == ControllerSide.Right));
    }

    public bool TryGetControllerPlacementPreviewPoint(
        OVRInput.Controller controller, out Vector3 worldPoint)
    {
        worldPoint = default;

        if (!IsControllerHoldingObject(controller) ||
            !controllerPreviewValid || tableReference == null ||
            !tableReference.HasReference)
        {
            return false;
        }

        worldPoint = tableReference.AnchorTransform.TransformPoint(
            controllerPreviewLocal);
        return true;
    }

    public bool TryGetControllerAimTarget(Ray ray, out Vector3 worldPoint)
    {
        worldPoint = default;

        if (!sessionStarted || completed || !enableControllers ||
            controllerHeldItem != null)
        {
            return false;
        }

        float bestScore = float.MaxValue;
        bool found = false;

        foreach (RecallItem item in items)
        {
            if (item.Source.Model == null || item.IsHandHeld ||
                IsHeldByOtherHand(item))
            {
                continue;
            }

            Bounds bounds = ModelBounds(item.Source.Model);
            Vector3 toward = bounds.center - ray.origin;
            float along = Vector3.Dot(toward, ray.direction);

            if (along <= 0f || along > maximumRayDistance)
                continue;

            Vector3 rayPoint = ray.GetPoint(along);
            float miss = Vector3.Distance(
                rayPoint, bounds.ClosestPoint(rayPoint));

            // Match the tolerance used by the existing controller grab.
            if (miss > controllerAimTolerance)
                continue;

            float score = miss * 10f + along * 0.01f;
            if (score >= bestScore)
                continue;

            bestScore = score;
            found = true;

            // Put the visible dot on the model, if the ray intersects it.
            worldPoint = bounds.IntersectRay(ray, out float hitDistance)
                ? ray.GetPoint(hitDistance)
                : rayPoint;
        }

        return found;
    }

    public bool IsObjectPlaced(int index)
    {
        return index >= 0 &&
               index < items.Count &&
               items[index].Placed;
    }

    private void UpdateHand(HandState state)
    {
        if (state.Hand == null || state.Skeleton == null)
            return;

        if (!TryGetPinchPosition(state, out Vector3 pinchPosition))
        {
            if (Time.time - state.LastTrackedTime > trackingLossGracePeriod)
            {
                if (state.HeldItem != null)
                    ReleaseHand(state, true);
                else
                    state.PinchActive = false;
            }

            return;
        }

        state.LastTrackedTime = Time.time;

        float strength = state.Hand.GetFingerPinchStrength(
            OVRHand.HandFinger.Index
        );

        if (!state.PinchActive && strength >= pinchStartThreshold)
        {
            state.PinchActive = true;
            TryBeginHandGrab(state, pinchPosition);
        }

        if (state.PinchActive && state.HeldItem != null)
            UpdateHandPlacement(state, pinchPosition);

        if (state.PinchActive && strength <= pinchReleaseThreshold)
        {
            state.PinchActive = false;
            ReleaseHand(state, false);
        }
    }

    private bool TryGetPinchPosition(
        HandState state,
        out Vector3 position)
    {
        position = default;

        if (!state.Hand.IsTracked || !state.Hand.IsDataValid)
            return false;

        if (!state.Skeleton.IsInitialized ||
            state.Skeleton.Bones == null)
        {
            return false;
        }

        Transform thumbTip = null;
        Transform indexTip = null;

        foreach (OVRBone bone in state.Skeleton.Bones)
        {
            if (bone == null)
                continue;

            if (bone.Id == OVRSkeleton.BoneId.Hand_ThumbTip)
                thumbTip = bone.Transform;

            if (bone.Id == OVRSkeleton.BoneId.Hand_IndexTip)
                indexTip = bone.Transform;
        }

        if (thumbTip == null || indexTip == null)
            return false;

        position = (thumbTip.position + indexTip.position) * 0.5f;

        return true;
    }

    private void TryBeginHandGrab(
        HandState state,
        Vector3 pinchPosition)
    {
        if (controllerHeldItem != null || state.HeldItem != null)
            return;

        RecallItem best = null;
        float bestDistance = handGrabRadius;

        foreach (RecallItem item in items)
        {
            if (item.Source.Model == null ||
                item.IsHandHeld ||
                IsHeldByOtherHand(item))
            {
                continue;
            }

            Bounds bounds = ModelBounds(item.Source.Model);

            float distance = Vector3.Distance(
                pinchPosition,
                bounds.ClosestPoint(pinchPosition)
            );

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = item;
        }

        if (best == null)
            return;

        state.HeldItem = best;

        best.IsHandHeld = true;
        best.WasPlacedBeforeGrab = best.Placed;
        best.PreviousLocalPosition = best.RecalledLocalPosition;

        Transform model = best.Source.Model;

        model.SetParent(tableReference.AnchorTransform, true);
        model.localScale = best.Source.OriginalLocalScale;

        state.GrabOffset = model.position - pinchPosition;

        spatialInventory.SetRecallSlotOccupied(
            best.Source.SlotIndex,
            false
        );

        SetStatus(
            $"{best.Source.CocoLabel}: grabbed with hand."
        );

        ObjectGrabbed?.Invoke(best.Source.CocoLabel);
    }

    private void UpdateHandPlacement(
        HandState state,
        Vector3 pinchPosition)
    {
        RecallItem item = state.HeldItem;

        if (item == null || item.Source.Model == null)
            return;

        item.Source.Model.position =
            pinchPosition + state.GrabOffset;
    }

    private void ReleaseHand(HandState state, bool cancelled)
    {
        RecallItem item = state.HeldItem;

        state.HeldItem = null;
        state.PinchActive = false;

        if (item == null)
            return;

        item.IsHandHeld = false;

        if (item.Source.Model == null)
            return;

        if (cancelled)
        {
            RestorePreviousPosition(item);
            SetStatus("Hand tracking lost. Object restored.");
            return;
        }

        Vector3 visualOffsetWorld =
            tableReference.AnchorTransform.TransformVector(
                item.Source.VisualOffsetLocal
            );

        Vector3 tablePoint =
            item.Source.Model.position - visualOffsetWorld;

        if (!TryValidateTablePosition(
            tablePoint,
            out Vector3 recalledLocal))
        {
            RestorePreviousPosition(item);

            SetStatus(
                "Please release the object above the table."
            );

            return;
        }

        CommitPlacement(item, recalledLocal);
    }

    private bool IsHeldByOtherHand(RecallItem item)
    {
        return leftState.HeldItem == item ||
               rightState.HeldItem == item;
    }

    private void ResetHandState(HandState state)
    {
        state.PinchActive = false;
        state.HeldItem = null;
        state.GrabOffset = Vector3.zero;
        state.LastTrackedTime = Time.time;
    }

    private void UpdateControllers()
    {
        if (controllerHeldItem != null)
        {
            if (IsControllerPressed(controllerSide))
                UpdateControllerPlacement();
            else
                ReleaseController();

            return;
        }

        if (IsControllerPressed(ControllerSide.Left))
        {
            TryBeginControllerGrab(
                leftControllerAnchor,
                ControllerSide.Left
            );
        }

        if (controllerHeldItem == null &&
            IsControllerPressed(ControllerSide.Right))
        {
            TryBeginControllerGrab(
                rightControllerAnchor,
                ControllerSide.Right
            );
        }
    }

    private void TryBeginControllerGrab(
        Transform controller,
        ControllerSide side)
    {
        if (controller == null)
            return;

        RecallItem best = null;
        float bestScore = float.MaxValue;

        Ray ray = new Ray(
            controller.position,
            controller.forward
        );

        foreach (RecallItem item in items)
        {
            if (item.Source.Model == null || item.IsHandHeld)
                continue;

            Bounds bounds = ModelBounds(item.Source.Model);

            float near = Vector3.Distance(
                bounds.ClosestPoint(controller.position),
                controller.position
            );

            float score;

            if (near <= controllerNearGrabRadius)
            {
                score = near;
            }
            else
            {
                Vector3 toward = bounds.center - ray.origin;
                float along = Vector3.Dot(
                    toward,
                    ray.direction
                );

                if (along <= 0f || along > maximumRayDistance)
                    continue;

                Vector3 rayPoint = ray.GetPoint(along);

                float miss = Vector3.Distance(
                    rayPoint,
                    bounds.ClosestPoint(rayPoint)
                );

                if (miss > controllerAimTolerance)
                    continue;

                score = miss * 10f + along * 0.01f;
            }

            if (score >= bestScore)
                continue;

            bestScore = score;
            best = item;
        }

        if (best == null)
            return;

        controllerHeldItem = best;
        controllerSide = side;
        controllerPreviewValid = false;

        best.WasPlacedBeforeGrab = best.Placed;
        best.PreviousLocalPosition = best.RecalledLocalPosition;

        best.Source.Model.SetParent(
            tableReference.AnchorTransform,
            true
        );

        best.Source.Model.localRotation =
            best.Source.OriginalLocalRotation;

        best.Source.Model.localScale =
            best.Source.OriginalLocalScale;

        // Keep the model's initial position relative to the controller ray.
        Ray grabRay = new Ray(controller.position, controller.forward);
        Vector3 modelCenter = ModelBounds(best.Source.Model).center;
        controllerGrabDistance = Mathf.Clamp(
            Vector3.Dot(modelCenter - grabRay.origin, grabRay.direction),
            0.2f,
            maximumRayDistance
        );
        controllerGrabOffset = best.Source.Model.position -
                               grabRay.GetPoint(controllerGrabDistance);

        spatialInventory.SetRecallSlotOccupied(
            best.Source.SlotIndex,
            false
        );

        SetStatus(
            $"{best.Source.CocoLabel}: controller selected."
        );

        ObjectGrabbed?.Invoke(best.Source.CocoLabel);

        UpdateControllerPlacement();
    }

    private void UpdateControllerPlacement()
    {
        if (controllerHeldItem == null ||
            controllerHeldItem.Source.Model == null)
        {
            return;
        }

        Transform controller =
            controllerSide == ControllerSide.Left
                ? leftControllerAnchor
                : rightControllerAnchor;

        if (controller == null)
        {
            controllerPreviewValid = false;
            return;
        }

        Ray ray = new Ray(controller.position, controller.forward);

        // Only snap to the table while the projected position is valid.
        if (tableReference.TryProjectRay(ray, out Vector3 point) &&
            TryValidateTablePosition(point, out Vector3 local))
        {
            controllerPreviewLocal = local;
            controllerPreviewValid = true;

            Transform anchor = tableReference.AnchorTransform;
            controllerHeldItem.Source.Model.localPosition =
                local +
                controllerHeldItem.Source.VisualOffsetLocal +
                anchor.InverseTransformVector(Vector3.up * previewLift);
            return;
        }

        // Keep the grabbed object movable even away from the table.
        controllerPreviewValid = false;
        controllerHeldItem.Source.Model.position =
            ray.GetPoint(controllerGrabDistance) + controllerGrabOffset;
    }

    private void ReleaseController()
    {
        RecallItem item = controllerHeldItem;

        bool valid = controllerPreviewValid;
        Vector3 local = controllerPreviewLocal;

        controllerHeldItem = null;
        controllerSide = ControllerSide.None;
        controllerPreviewValid = false;
        controllerGrabDistance = 0f;
        controllerGrabOffset = Vector3.zero;

        if (item == null || item.Source.Model == null)
            return;

        if (!valid)
        {
            RestorePreviousPosition(item);
            SetStatus("Aim at the table before releasing.");
        }
        else
        {
            CommitPlacement(item, local);
        }
    }

    private void CommitPlacement(
        RecallItem item,
        Vector3 recalledLocal)
    {
        if (item.Source.Model == null)
            return;

        bool isCorrection = item.WasPlacedBeforeGrab;

        item.Source.Model.SetParent(
            tableReference.AnchorTransform,
            true
        );

        item.Source.Model.localPosition =
            recalledLocal + item.Source.VisualOffsetLocal;

        item.Source.Model.localRotation =
            item.Source.OriginalLocalRotation;

        item.Source.Model.localScale =
            item.Source.OriginalLocalScale;

        item.RecalledLocalPosition = recalledLocal;
        item.Placed = true;

        item.PlacementCount++;

        if (isCorrection)
        {
            item.CorrectionCount++;
            totalCorrections++;
        }

        spatialInventory.SetRecallSlotOccupied(
            item.Source.SlotIndex,
            false
        );

        PlacedCount = CountPlaced();

        SetStatus(
            $"Placed {PlacedCount} of {items.Count} objects."
        );

        ObjectPlaced?.Invoke(
            item.Source.CocoLabel,
            isCorrection
        );

        NotifyProgress();
    }

    private void RestorePreviousPosition(RecallItem item)
    {
        if (item.WasPlacedBeforeGrab)
        {
            item.Source.Model.SetParent(
                tableReference.AnchorTransform,
                true
            );

            item.Source.Model.localPosition =
                item.PreviousLocalPosition +
                item.Source.VisualOffsetLocal;

            item.Source.Model.localRotation =
                item.Source.OriginalLocalRotation;

            item.Source.Model.localScale =
                item.Source.OriginalLocalScale;

            item.RecalledLocalPosition =
                item.PreviousLocalPosition;

            item.Placed = true;

            spatialInventory.SetRecallSlotOccupied(
                item.Source.SlotIndex,
                false
            );
        }
        else
        {
            RestoreToInventory(item);
        }

        PlacedCount = CountPlaced();
        NotifyProgress();
    }

    private void RestoreToInventory(RecallItem item)
    {
        if (item.Source.Model == null)
            return;

        item.Source.Model.SetParent(
            spatialInventory.transform,
            true
        );

        item.Source.Model.position =
            item.DockWorldPosition;

        item.Source.Model.rotation =
            item.DockWorldRotation;

        item.Source.Model.localScale =
            item.DockLocalScale;

        item.Placed = false;

        spatialInventory.SetRecallSlotOccupied(
            item.Source.SlotIndex,
            true
        );
    }

    private bool TryValidateTablePosition(
        Vector3 worldPoint,
        out Vector3 local)
    {
        local = default;

        if (tableReference == null ||
            !tableReference.HasReference)
        {
            return false;
        }

        if (!IsFinite(worldPoint))
            return false;

        worldPoint.y = tableReference.TableHeight;

        local = tableReference.AnchorTransform
            .InverseTransformPoint(worldPoint);

        local.y = 0f;

        float dx =
            local.x - tableCenterOffsetLocal.x;

        float dz =
            local.z - tableCenterOffsetLocal.y;

        if (dx * dx + dz * dz >
            maximumPlacementRadius * maximumPlacementRadius)
        {
            return false;
        }

        Vector3 inventory =
            spatialInventory.transform.position;

        return Vector2.Distance(
            new Vector2(worldPoint.x, worldPoint.z),
            new Vector2(inventory.x, inventory.z)
        ) >= minimumDistanceFromInventory;
    }

    private static Bounds ModelBounds(Transform model)
    {
        bool found = false;

        Bounds result = new Bounds(
            model.position,
            Vector3.one * 0.04f
        );

        foreach (Renderer renderer in
            model.GetComponentsInChildren<Renderer>())
        {
            if (!renderer.enabled)
                continue;

            if (!found)
            {
                result = renderer.bounds;
                found = true;
            }
            else
            {
                result.Encapsulate(renderer.bounds);
            }
        }

        return result;
    }

    private bool IsControllerPressed(ControllerSide side)
    {
        if (!enableControllers ||
            side == ControllerSide.None)
        {
            return false;
        }

        OVRInput.Controller controller =
            side == ControllerSide.Left
                ? OVRInput.Controller.LTouch
                : OVRInput.Controller.RTouch;

        return OVRInput.Get(
            OVRInput.Axis1D.PrimaryIndexTrigger,
            controller
        ) >= triggerThreshold ||
        (
            allowGripTrigger &&
            OVRInput.Get(
                OVRInput.Axis1D.PrimaryHandTrigger,
                controller
            ) >= triggerThreshold
        );
    }

    private int CountPlaced()
    {
        int count = 0;

        foreach (RecallItem item in items)
        {
            if (item.Placed)
                count++;
        }

        return count;
    }

    private void NotifyProgress()
    {
        RecallProgressChanged?.Invoke(
            PlacedCount,
            items.Count
        );
    }

    [ContextMenu("Engram/Reset Recall To Inventory")]
    public void ResetRecallToInventory()
    {
        if (!sessionStarted ||
            tableReference == null ||
            !tableReference.HasReference)
        {
            return;
        }

        if (leftState.HeldItem != null)
            ReleaseHand(leftState, true);

        if (rightState.HeldItem != null)
            ReleaseHand(rightState, true);

        controllerHeldItem = null;
        controllerSide = ControllerSide.None;
        controllerPreviewValid = false;
        controllerGrabDistance = 0f;
        controllerGrabOffset = Vector3.zero;

        foreach (RecallItem item in items)
        {
            if (item.Source.Model == null)
                continue;

            RestoreToInventory(item);

            item.RecalledLocalPosition = default;
            item.PreviousLocalPosition = default;

            item.Placed = false;
            item.IsHandHeld = false;
            item.WasPlacedBeforeGrab = false;

            item.PlacementCount = 0;
            item.CorrectionCount = 0;
        }

        ResetHandState(leftState);
        ResetHandState(rightState);

        completed = false;
        PlacedCount = 0;
        totalCorrections = 0;

        LastResult = null;
        LastSavedResultPath = null;
        LastSaveError = null;
        ResultSavedSuccessfully = false;

        recallStartTime = Time.realtimeSinceStartupAsDouble;

        SetStatus(
            "Recall reset. All objects are back in the inventory."
        );

        RecallReset?.Invoke();
        NotifyProgress();
    }

    [ContextMenu("Engram/Finish Recall")]
    public void FinishRecall()
    {
        TryFinishRecall();
    }

    public bool TryFinishRecall()
    {
        if (!CanFinishRecall)
        {
            SetStatus(
                "Please place all four objects before finishing."
            );

            return false;
        }

        if (!ValidateSession(out string validationError))
        {
            SetStatus(
                "Result validation failed: " + validationError
            );

            Debug.LogWarning(
                "Engram Recall: " + validationError
            );

            return false;
        }

        RecallResult result = new RecallResult
        {
            timestampUtc = DateTime.UtcNow.ToString(
                "o",
                CultureInfo.InvariantCulture
            ),

            anchorUuid = tableReference.AnchorUuid.ToString(),
            objectCount = items.Count,
            durationSeconds = ElapsedSeconds,
            totalCorrections = totalCorrections
        };

        float sum = 0f;

        foreach (RecallItem item in items)
        {
            Vector3 reference =
                item.Source.ReferenceLocalPosition;

            Vector3 recalled =
                item.RecalledLocalPosition;

            float error = new Vector2(
                recalled.x - reference.x,
                recalled.z - reference.z
            ).magnitude * 100f;

            sum += error;

            result.objects.Add(new ObjectResult
            {
                cocoLabel = item.Source.CocoLabel,
                referenceLocalPosition = reference,
                recalledLocalPosition = recalled,
                horizontalErrorCm = error,
                placementCount = item.PlacementCount,
                correctionCount = item.CorrectionCount
            });
        }

        result.meanHorizontalErrorCm =
            sum / items.Count;

        if (!ValidateResult(result, out validationError))
        {
            SetStatus(
                "Result validation failed: " + validationError
            );

            return false;
        }

        LastResult = result;
        completed = true;

        LastSavedResultPath = null;
        ResultSavedSuccessfully = false;
        LastSaveError = null;

        if (saveResultAsJson)
            SaveAndVerifyResult(result);

        SetStatus(
            $"Completed. Mean horizontal deviation: " +
            $"{result.meanHorizontalErrorCm:F1} cm."
        );

        RecallCompleted?.Invoke(result);

        return true;
    }

    private bool ValidateSession(out string error)
    {
        error = "";

        if (items.Count != 4)
        {
            error = "Exactly four objects are required.";
            return false;
        }

        HashSet<string> labels =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RecallItem item in items)
        {
            if (item.Source.Model == null)
            {
                error = "An object model is missing.";
                return false;
            }

            if (!item.Placed || item.PlacementCount < 1)
            {
                error = "Not all objects have valid placements.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.Source.CocoLabel) ||
                !labels.Add(item.Source.CocoLabel))
            {
                error = "Object labels are missing or duplicated.";
                return false;
            }

            if (!IsFinite(item.Source.ReferenceLocalPosition) ||
                !IsFinite(item.RecalledLocalPosition))
            {
                error = "Invalid object coordinates.";
                return false;
            }

            if (item.CorrectionCount != item.PlacementCount - 1)
            {
                error = "Inconsistent correction count.";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateResult(
        RecallResult result,
        out string error)
    {
        error = "";

        if (result == null ||
            result.objects == null ||
            result.objectCount != 4 ||
            result.objects.Count != 4)
        {
            error = "Invalid result object count.";
            return false;
        }

        if (!IsFinite(result.durationSeconds) ||
            result.durationSeconds < 0f)
        {
            error = "Invalid task duration.";
            return false;
        }

        if (!IsFinite(result.meanHorizontalErrorCm) ||
            result.meanHorizontalErrorCm < 0f)
        {
            error = "Invalid mean position error.";
            return false;
        }

        HashSet<string> labels =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        float sum = 0f;
        int corrections = 0;

        foreach (ObjectResult item in result.objects)
        {
            if (item == null ||
                string.IsNullOrWhiteSpace(item.cocoLabel) ||
                !labels.Add(item.cocoLabel))
            {
                error = "Invalid or duplicate object label.";
                return false;
            }

            if (!IsFinite(item.referenceLocalPosition) ||
                !IsFinite(item.recalledLocalPosition))
            {
                error = "Invalid object position.";
                return false;
            }

            float calculatedError = new Vector2(
                item.recalledLocalPosition.x -
                item.referenceLocalPosition.x,

                item.recalledLocalPosition.z -
                item.referenceLocalPosition.z
            ).magnitude * 100f;

            if (!IsFinite(calculatedError) ||
                Mathf.Abs(calculatedError - item.horizontalErrorCm) > 0.001f)
            {
                error = "Inconsistent position error.";
                return false;
            }

            if (item.placementCount < 1 ||
                item.correctionCount < 0 ||
                item.correctionCount != item.placementCount - 1)
            {
                error = "Invalid placement history.";
                return false;
            }

            sum += calculatedError;
            corrections += item.correctionCount;
        }

        if (Mathf.Abs(
            sum / result.objectCount -
            result.meanHorizontalErrorCm) > 0.001f)
        {
            error = "Mean position error does not match individual errors.";
            return false;
        }

        if (corrections != result.totalCorrections)
        {
            error = "Total correction count does not match.";
            return false;
        }

        return true;
    }

    private void SaveAndVerifyResult(RecallResult result)
    {
        try
        {
            string filename =
                "EngramRecall_" +
                DateTime.UtcNow.ToString(
                    "yyyyMMdd_HHmmss_fff",
                    CultureInfo.InvariantCulture
                ) +
                ".json";

            string path = Path.Combine(
                Application.persistentDataPath,
                filename
            );

            File.WriteAllText(
                path,
                JsonUtility.ToJson(result, true)
            );

            string savedJson = File.ReadAllText(path);

            RecallResult savedResult =
                JsonUtility.FromJson<RecallResult>(savedJson);

            if (!ValidateResult(
                savedResult,
                out string validationError))
            {
                throw new InvalidDataException(
                    validationError
                );
            }

            if (savedResult.anchorUuid != result.anchorUuid ||
                savedResult.timestampUtc != result.timestampUtc)
            {
                throw new InvalidDataException(
                    "Saved result metadata does not match."
                );
            }

            LastSavedResultPath = path;
            ResultSavedSuccessfully = true;

            Debug.Log(
                "Engram Recall: Results saved and verified: " + path
            );
        }
        catch (Exception exception)
        {
            ResultSavedSuccessfully = false;
            LastSaveError = exception.Message;

            Debug.LogWarning(
                "Engram Recall: Result saving failed: " +
                exception.Message
            );
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) &&
               !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) &&
               IsFinite(value.y) &&
               IsFinite(value.z);
    }

    private void ClearSession()
    {
        items.Clear();
        sourceBuffer.Clear();

        ResetHandState(leftState);
        ResetHandState(rightState);

        controllerHeldItem = null;
        controllerSide = ControllerSide.None;
        controllerPreviewValid = false;
        controllerGrabDistance = 0f;
        controllerGrabOffset = Vector3.zero;

        sessionStarted = false;
        completed = false;

        PlacedCount = 0;
        totalCorrections = 0;

        LastResult = null;
        LastSavedResultPath = null;
        LastSaveError = null;
        ResultSavedSuccessfully = false;

        if (spatialInventory != null)
            spatialInventory.EndRecall();

        NotifyProgress();
    }

    private void OnDisable()
    {
        ClearSession();
    }

    private void SetStatus(string message)
    {
        if (message == lastStatus)
            return;

        lastStatus = message;

        if (optionalStatusText != null)
            optionalStatusText.text = message;

        Debug.Log("Engram Recall: " + message);

        StatusChanged?.Invoke(message);
    }
}