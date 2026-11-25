using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Central spoken guidance. Sound effects remain in EngramAudioManager.
public class EngramVoiceManager : MonoBehaviour
{
    [Serializable]
    private class VoiceMuteButton
    {
        public Button button;
        public Image icon;
    }

    [Serializable]
    private class ObjectVoiceClip
    {
        public string cocoLabel;
        public AudioClip clip;
    }

    private enum VoicePhase
    {
        None,
        Setup1,
        Setup2,
        Setup3,
        Capture,
        Inventory,
        Recall,
        AllPlaced,
        Results
    }

    [Header("Engram References")]
    [SerializeField] private EngramSessionFlow sessionFlow;
    [SerializeField] private EngramSpatialObjectMarkers spatialMarkers;
    [SerializeField] private EngramSpatialInventory spatialInventory;
    [SerializeField] private EngramMemoryRecall memoryRecall;

    [Header("Voice Output (2D)")]
    [SerializeField] private AudioSource voiceSource;
    [SerializeField, Range(0f, 1f)] private float voiceVolume = 0.85f;
    [SerializeField, Min(0f)] private float welcomeDelay = 1.1f;
    [SerializeField, Min(0f)] private float resultsDelay = 0.8f;
    [SerializeField, Range(0f, 1f)] private float clipGap = 0.12f;
    [SerializeField, Range(0f, 1f)] private float nameGap = 0.22f;

    [Header("Setup Voice Clips")]
    [SerializeField] private AudioClip welcomeClip;                // 01_Welcome
    [SerializeField] private AudioClip tableCalibrationClip;       // 02_Setup_TableCalibration
    [SerializeField] private AudioClip objectCaptureClip;          // 03_Setup_ObjectCapture
    [SerializeField] private AudioClip memoryPreparationClip;      // 04_Setup_MemoryPreparation

    [Header("Capture and Inventory")]
    [SerializeField] private AudioClip captureSavedClip;           // 05_Capture_Saved
    [SerializeField] private AudioClip inventoryReadyClip;         // 06_Inventory_Ready

    [Header("Recall")]
    [SerializeField] private AudioClip recallStartClip;            // 07_Recall_Start
    [SerializeField] private AudioClip allPlacedIntroClip;         // 08_Recall_AllPlaced_Intro
    [SerializeField] private AudioClip andClip;                    // 09_Recall_And (optional)
    [SerializeField] private AudioClip allPlacedOutroClip;         // 10_Recall_AllPlaced_Outro
    [SerializeField] private AudioClip recallResetClip;            // 11_Recall_Reset

    [Header("Results")]
    [SerializeField] private AudioClip resultsCompleteClip;        // 12_Results_Complete
    [SerializeField] private AudioClip resultsRestartClip;         // 13_Results_Restart (optional)

    [Header("Object Names (each clip says only the name)")]
    [SerializeField] private List<ObjectVoiceClip> objectNames = new()
    {
        new ObjectVoiceClip { cocoLabel = "apple" },
        new ObjectVoiceClip { cocoLabel = "banana" },
        new ObjectVoiceClip { cocoLabel = "book" },
        new ObjectVoiceClip { cocoLabel = "bottle" },
        new ObjectVoiceClip { cocoLabel = "bowl" },
        new ObjectVoiceClip { cocoLabel = "cell phone" },
        new ObjectVoiceClip { cocoLabel = "clock" },
        new ObjectVoiceClip { cocoLabel = "cup" },
        new ObjectVoiceClip { cocoLabel = "scissors" },
        new ObjectVoiceClip { cocoLabel = "wine glass" }
    };

    [Header("Global Voice Mute Buttons")]
    [SerializeField] private List<VoiceMuteButton> muteButtons = new();
    [SerializeField] private Sprite voiceOnIcon;
    [SerializeField] private Sprite voiceOffIcon;
    [SerializeField] private Color voiceOnColor = Color.white;
    [SerializeField] private Color voiceOffColor = new Color(0.64f, 0.73f, 0.75f, 1f);
    [SerializeField] private bool rememberMuteAcrossLaunches = true;

    private const string MutePreferenceKey = "Engram.Voice.Muted";

    private struct VoiceStep
    {
        public AudioClip Clip;
        public float DelayBefore;
        public float GapAfter;
    }

    private readonly Queue<VoiceStep> voiceQueue = new();
    private readonly List<string> placementOrder = new();
    private readonly HashSet<Button> connectedMuteButtons = new();

    // Buttons are listed here so the custom XR pointer can hit-test them.
    public IEnumerable<Button> MuteButtons
    {
        get
        {
            foreach (VoiceMuteButton binding in muteButtons)
            {
                if (binding != null && binding.button != null)
                    yield return binding.button;
            }
        }
    }

    private Coroutine playbackRoutine;
    private VoicePhase currentPhase = VoicePhase.None;
    private bool muted;
    private bool welcomePlayed;
    private bool allPlacedAnnounced;
    private bool reportedMissingAnd;

    private void Awake()
    {
        if (sessionFlow == null)
            sessionFlow = FindFirstObjectByType<EngramSessionFlow>();
        if (spatialMarkers == null)
            spatialMarkers = FindFirstObjectByType<EngramSpatialObjectMarkers>();
        if (spatialInventory == null)
            spatialInventory = FindFirstObjectByType<EngramSpatialInventory>();
        if (memoryRecall == null)
            memoryRecall = FindFirstObjectByType<EngramMemoryRecall>();

        if (voiceSource == null)
        {
            voiceSource = GetComponent<AudioSource>();
            if (voiceSource == null)
                voiceSource = gameObject.AddComponent<AudioSource>();
        }

        voiceSource.playOnAwake = false;
        voiceSource.loop = false;
        voiceSource.spatialBlend = 0f;
        voiceSource.volume = voiceVolume;
        muted = rememberMuteAcrossLaunches && PlayerPrefs.GetInt(MutePreferenceKey, 0) != 0;
    }

    private void OnEnable()
    {
        if (sessionFlow != null)
        {
            sessionFlow.SetupOpened += OnSetupOpened;
            Subscribe(sessionFlow.NextButton, OnNextPage);
            Subscribe(sessionFlow.NextButtonPage2, OnNextPage);
            Subscribe(sessionFlow.SkipButtonPage1, OnSetupClosed);
            Subscribe(sessionFlow.SkipButtonPage2, OnSetupClosed);
            Subscribe(sessionFlow.StartButton, OnSetupClosed);
        }

        if (spatialMarkers != null)
            spatialMarkers.PositionsSaved += OnPositionsSaved;
        if (spatialInventory != null)
            spatialInventory.AllObjectsDocked += OnInventoryReady;
        if (memoryRecall != null)
        {
            memoryRecall.RecallStarted += OnRecallStarted;
            memoryRecall.RecallReset += OnRecallReset;
            memoryRecall.ObjectPlaced += OnObjectPlaced;
            memoryRecall.RecallCompleted += OnRecallCompleted;
        }

        connectedMuteButtons.Clear();
        foreach (VoiceMuteButton binding in muteButtons)
        {
            if (binding?.button != null && connectedMuteButtons.Add(binding.button))
                binding.button.onClick.AddListener(ToggleVoice);
        }
        RefreshMuteIcons();
    }

    private void Start()
    {
        // Handles the unusual case of a scene without a SessionFlow component.
        if (sessionFlow == null)
        {
            currentPhase = VoicePhase.Setup1;
            QueueClip(welcomeClip, welcomeDelay);
            QueueClip(tableCalibrationClip);
            welcomePlayed = true;
        }
    }

    private void OnDisable()
    {
        if (sessionFlow != null)
        {
            sessionFlow.SetupOpened -= OnSetupOpened;
            Unsubscribe(sessionFlow.NextButton, OnNextPage);
            Unsubscribe(sessionFlow.NextButtonPage2, OnNextPage);
            Unsubscribe(sessionFlow.SkipButtonPage1, OnSetupClosed);
            Unsubscribe(sessionFlow.SkipButtonPage2, OnSetupClosed);
            Unsubscribe(sessionFlow.StartButton, OnSetupClosed);
        }
        if (spatialMarkers != null)
            spatialMarkers.PositionsSaved -= OnPositionsSaved;
        if (spatialInventory != null)
            spatialInventory.AllObjectsDocked -= OnInventoryReady;
        if (memoryRecall != null)
        {
            memoryRecall.RecallStarted -= OnRecallStarted;
            memoryRecall.RecallReset -= OnRecallReset;
            memoryRecall.ObjectPlaced -= OnObjectPlaced;
            memoryRecall.RecallCompleted -= OnRecallCompleted;
        }
        foreach (Button button in connectedMuteButtons)
            if (button != null)
                button.onClick.RemoveListener(ToggleVoice);
        connectedMuteButtons.Clear();
        StopNarration();
    }

    private void Subscribe(Button button, UnityEngine.Events.UnityAction callback)
    {
        if (button != null)
            button.onClick.AddListener(callback);
    }

    private void Unsubscribe(Button button, UnityEngine.Events.UnityAction callback)
    {
        if (button != null)
            button.onClick.RemoveListener(callback);
    }

    private void OnSetupOpened()
    {
        bool restartingFromResults = currentPhase == VoicePhase.Results;
        StopNarration();
        placementOrder.Clear();
        allPlacedAnnounced = false;
        currentPhase = VoicePhase.Setup1;

        if (!welcomePlayed)
        {
            welcomePlayed = true;
            QueueClip(welcomeClip, welcomeDelay);
        }
        else if (restartingFromResults)
        {
            QueueClip(resultsRestartClip);
        }
        QueueClip(tableCalibrationClip);
    }

    private void OnNextPage()
    {
        if (sessionFlow == null || !sessionFlow.InstructionsVisible)
            return;

        StopNarration();
        switch (sessionFlow.CurrentSetupPageIndex)
        {
            case 1:
                currentPhase = VoicePhase.Setup2;
                QueueClip(objectCaptureClip);
                break;
            case 2:
                currentPhase = VoicePhase.Setup3;
                QueueClip(memoryPreparationClip);
                break;
        }
    }

    private void OnSetupClosed()
    {
        StopNarration();
        currentPhase = VoicePhase.Capture;
        if (memoryRecall != null && memoryRecall.SessionStarted && !memoryRecall.Completed)
            OnRecallStarted();
    }

    private bool IsIntroductionVisible() =>
        sessionFlow != null && sessionFlow.InstructionsVisible;

    private void OnPositionsSaved(int count)
    {
        if (count != 4 || IsIntroductionVisible() ||
            (memoryRecall != null && memoryRecall.SessionStarted))
            return;

        currentPhase = VoicePhase.Capture;
        QueueClip(captureSavedClip);
    }

    private void OnInventoryReady()
    {
        if (IsIntroductionVisible() ||
            (memoryRecall != null && memoryRecall.Completed))
            return;

        currentPhase = VoicePhase.Inventory;
        QueueClip(inventoryReadyClip);
    }

    private void OnRecallStarted()
    {
        placementOrder.Clear();
        allPlacedAnnounced = false;
        currentPhase = VoicePhase.Recall;
        if (!IsIntroductionVisible())
            QueueClip(recallStartClip);
    }

    private void OnRecallReset()
    {
        placementOrder.Clear();
        allPlacedAnnounced = false;
        currentPhase = VoicePhase.Recall;
        StopNarration();
        QueueClip(recallResetClip);
    }

    private void OnObjectPlaced(string cocoLabel, bool isCorrection)
    {
        if (isCorrection || IsIntroductionVisible() ||
            memoryRecall == null || memoryRecall.Completed || allPlacedAnnounced)
            return;

        if (!string.IsNullOrWhiteSpace(cocoLabel) && !placementOrder.Contains(cocoLabel))
            placementOrder.Add(cocoLabel);

        if (!memoryRecall.CanFinishRecall)
            return;

        allPlacedAnnounced = true;
        currentPhase = VoicePhase.AllPlaced;

        // No obsolete setup or recall instruction should play after the fourth placement.
        StopNarration();
        BuildAllPlacedAnnouncement();
    }

    private void BuildAllPlacedAnnouncement()
    {
        QueueClip(allPlacedIntroClip);

        var names = new List<string>();
        foreach (string label in placementOrder)
            if (!names.Contains(label))
                names.Add(label);

        // Fallback if placements were not all observed, e.g. after enable/disable.
        if (memoryRecall != null)
        {
            for (int i = 0; i < memoryRecall.ObjectCount; i++)
            {
                string label = memoryRecall.GetObjectLabel(i);
                if (!string.IsNullOrWhiteSpace(label) && !names.Contains(label))
                    names.Add(label);
            }
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (i == names.Count - 1 && names.Count > 1 && andClip != null)
                QueueClip(andClip, 0f, nameGap);
            else if (i == names.Count - 1 && names.Count > 1 && andClip == null && !reportedMissingAnd)
            {
                Debug.LogWarning("Engram Voice: '09_Recall_And' is not assigned. The list will play without 'und'.");
                reportedMissingAnd = true;
            }

            AudioClip nameClip = GetNameClip(names[i]);
            if (nameClip == null)
                Debug.LogWarning("Engram Voice: Missing object name audio for '" + names[i] + "'.");
            QueueClip(nameClip, 0f, nameGap);
        }
        QueueClip(allPlacedOutroClip);
    }

    private AudioClip GetNameClip(string label)
    {
        foreach (ObjectVoiceClip entry in objectNames)
        {
            if (entry != null && string.Equals(entry.cocoLabel?.Trim(), label?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return entry.clip;
        }
        return null;
    }

    private void OnRecallCompleted(EngramMemoryRecall.RecallResult result)
    {
        StopNarration();
        currentPhase = VoicePhase.Results;
        QueueClip(resultsCompleteClip, resultsDelay);
    }

    public void ToggleVoice() => SetMuted(!muted);

    public void SetMuted(bool value)
    {
        if (muted == value)
            return;

        muted = value;
        if (rememberMuteAcrossLaunches)
        {
            PlayerPrefs.SetInt(MutePreferenceKey, muted ? 1 : 0);
            PlayerPrefs.Save();
        }
        StopNarration();
        RefreshMuteIcons();

        if (!muted)
            RepeatCurrentInstruction();
    }

    // Repeats only the relevant current message, never the entire skipped queue.
    public void RepeatCurrentInstruction()
    {
        if (muted)
            return;

        switch (currentPhase)
        {
            case VoicePhase.Setup1: QueueClip(tableCalibrationClip); break;
            case VoicePhase.Setup2: QueueClip(objectCaptureClip); break;
            case VoicePhase.Setup3: QueueClip(memoryPreparationClip); break;
            case VoicePhase.Capture: break;
            case VoicePhase.Inventory: QueueClip(inventoryReadyClip); break;
            case VoicePhase.Recall: QueueClip(recallStartClip); break;
            case VoicePhase.AllPlaced: BuildAllPlacedAnnouncement(); break;
            case VoicePhase.Results: QueueClip(resultsCompleteClip); break;
        }
    }

    private void RefreshMuteIcons()
    {
        foreach (VoiceMuteButton binding in muteButtons)
        {
            if (binding?.icon == null)
                continue;
            Sprite sprite = muted ? voiceOffIcon : voiceOnIcon;
            if (sprite != null)
                binding.icon.sprite = sprite;
            binding.icon.color = muted ? voiceOffColor : voiceOnColor;
        }
    }

    private void QueueClip(AudioClip clip, float delayBefore = 0f, float gapAfter = -1f)
    {
        if (muted || clip == null || !isActiveAndEnabled || voiceSource == null)
            return;

        voiceQueue.Enqueue(new VoiceStep
        {
            Clip = clip,
            DelayBefore = Mathf.Max(0f, delayBefore),
            GapAfter = gapAfter >= 0f ? gapAfter : clipGap
        });
        if (playbackRoutine == null)
            playbackRoutine = StartCoroutine(PlayVoiceQueue());
    }

    private IEnumerator PlayVoiceQueue()
    {
        while (voiceQueue.Count > 0 && !muted)
        {
            VoiceStep step = voiceQueue.Dequeue();
            if (step.DelayBefore > 0f)
                yield return new WaitForSecondsRealtime(step.DelayBefore);

            if (muted || step.Clip == null)
                continue;
            voiceSource.volume = voiceVolume;
            voiceSource.clip = step.Clip;
            voiceSource.Play();
            while (voiceSource.isPlaying && !muted)
                yield return null;
            if (!muted && voiceQueue.Count > 0 && step.GapAfter > 0f)
                yield return new WaitForSecondsRealtime(step.GapAfter);
        }
        playbackRoutine = null;
    }

    private void StopNarration()
    {
        voiceQueue.Clear();
        if (playbackRoutine != null)
        {
            StopCoroutine(playbackRoutine);
            playbackRoutine = null;
        }
        if (voiceSource != null)
            voiceSource.Stop();
    }
}
