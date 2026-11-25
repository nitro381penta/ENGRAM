using UnityEngine;

// Plays short interface sounds without changing the existing voice narration.
public class EngramAudioManager : MonoBehaviour
{
    [Header("Engram References")]
    [SerializeField] private EngramSessionFlow sessionFlow;
    [SerializeField] private EngramMemoryRecall memoryRecall;
    [SerializeField] private EngramSpatialInventory spatialInventory;

    [Header("Audio Output (2D Effects)")]
    [SerializeField] private AudioSource effectsSource;
    [SerializeField, Range(0f, 1f)] private float masterVolume = 0.8f;

    [Header("Sound Clips")]
    [SerializeField] private AudioClip welcomeClip;
    [SerializeField] private AudioClip grabClip;
    [SerializeField] private AudioClip placementClip;
    [SerializeField] private AudioClip inventoryDockClip;
    [SerializeField] private AudioClip completionClip;

    [Header("Individual Volumes")]
    [SerializeField, Range(0f, 3f)] private float welcomeVolume = 0.75f;
    [SerializeField, Range(0f, 1f)] private float grabVolume = 0.35f;
    [SerializeField, Range(0f, 1f)] private float placementVolume = 0.6f;
    [SerializeField, Range(0f, 1f)] private float inventoryDockVolume = 0.65f;
    [SerializeField, Range(0f, 1f)] private float completionVolume = 0.75f;

    [Header("Behaviour")]
    [SerializeField] private bool playWelcomeOnLaunch = true;

    private bool welcomePlayed;

    private void Awake()
    {
        if (sessionFlow == null)
            sessionFlow = FindFirstObjectByType<EngramSessionFlow>();

        if (memoryRecall == null)
            memoryRecall = FindFirstObjectByType<EngramMemoryRecall>();

        if (spatialInventory == null)
            spatialInventory = FindFirstObjectByType<EngramSpatialInventory>();

        if (effectsSource == null)
        {
            effectsSource = GetComponent<AudioSource>();
            if (effectsSource == null)
                effectsSource = gameObject.AddComponent<AudioSource>();
        }

        effectsSource.playOnAwake = false;
        effectsSource.loop = false;
        effectsSource.spatialBlend = 0f;
    }

    private void OnEnable()
    {
        if (sessionFlow != null)
            sessionFlow.SetupOpened += OnSetupOpened;

        if (memoryRecall != null)
        {
            memoryRecall.ObjectGrabbed += OnObjectGrabbed;
            memoryRecall.ObjectPlaced += OnObjectPlaced;
            memoryRecall.RecallCompleted += OnRecallCompleted;
        }

        // No assigned clip: the inventory keeps playing its existing docking sound.
        if (spatialInventory != null && inventoryDockClip != null)
            spatialInventory.ObjectDocked += OnObjectDocked;
    }

    private void Start()
    {
        // Fallback if the scene does not contain a SessionFlow.
        if (sessionFlow == null)
            OnSetupOpened();
    }

    private void OnDisable()
    {
        if (sessionFlow != null)
            sessionFlow.SetupOpened -= OnSetupOpened;

        if (memoryRecall != null)
        {
            memoryRecall.ObjectGrabbed -= OnObjectGrabbed;
            memoryRecall.ObjectPlaced -= OnObjectPlaced;
            memoryRecall.RecallCompleted -= OnRecallCompleted;
        }

        if (spatialInventory != null)
            spatialInventory.ObjectDocked -= OnObjectDocked;
    }

    private void OnSetupOpened()
    {
        if (!playWelcomeOnLaunch || welcomePlayed)
            return;

        welcomePlayed = true;
        PlayWelcome();
    }

    private void OnObjectGrabbed(string cocoLabel) => PlayGrab();

    private void OnObjectPlaced(string cocoLabel, bool correction) => PlayPlacement();

    private void OnObjectDocked() => PlayInventoryDock();

    private void OnRecallCompleted(EngramMemoryRecall.RecallResult result) => PlayCompletion();

    public void PlayWelcome() => PlayEffect(welcomeClip, welcomeVolume);
    public void PlayGrab() => PlayEffect(grabClip, grabVolume);
    public void PlayPlacement() => PlayEffect(placementClip, placementVolume);
    public void PlayInventoryDock() => PlayEffect(inventoryDockClip, inventoryDockVolume);
    public void PlayCompletion() => PlayEffect(completionClip, completionVolume);

    private void PlayEffect(AudioClip clip, float volume)
    {
        if (!isActiveAndEnabled || effectsSource == null || clip == null)
            return;

        float level = Mathf.Clamp01(volume) * Mathf.Clamp01(masterVolume);
        if (level <= 0f)
            return;

        // PlayOneShot allows two close interactions without stopping each other.
        effectsSource.PlayOneShot(clip, level);
    }
}
