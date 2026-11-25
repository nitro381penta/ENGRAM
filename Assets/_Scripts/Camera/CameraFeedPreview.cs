using UnityEngine;
using UnityEngine.UI;
using Meta.XR;

public class CameraFeedPreview : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess cameraAccess;
    [SerializeField] private RawImage cameraPreview;

    private bool initialized;

    private void Update()
    {
        if (cameraAccess == null || cameraPreview == null)
            return;

        if (!cameraAccess.isActiveAndEnabled || !cameraAccess.IsPlaying)
            return;

        Texture cameraTexture = cameraAccess.GetTexture();

        if (cameraTexture == null)
            return;

        cameraPreview.texture = cameraTexture;

        if (!initialized)
        {
            Debug.Log($"Engram: Camera initialized ({cameraAccess.CurrentResolution})");
            initialized = true;
        }
    }
}