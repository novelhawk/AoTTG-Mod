using System;
using Mod.manager;
using UnityEngine;

[RequireComponent(typeof(Camera)), AddComponentMenu("NGUI/Tween/Field of View")]
public class TweenFOV : UITweener
{
    public float from;
    private Camera mCam;
    public float to;

    public static TweenFOV Begin(GameObject go, float duration, float to)
    {
        TweenFOV nfov = UITweener.Begin<TweenFOV>(go, duration);
        nfov.from = nfov.fov;
        nfov.to = to;
        if (duration <= 0f)
        {
            nfov.Sample(1f, true);
            nfov.enabled = false;
        }
        return nfov;
    }

    protected override void OnUpdate(float factor, bool isFinished)
    {
                        if (!ModManager.Find("module.customfov").Enabled)
        this.cachedCamera.fieldOfView = (this.from * (1f - factor)) + (this.to * factor);
    }

    public Camera cachedCamera
    {
        get
        {
            if (this.mCam == null)
            {
                this.mCam = base.camera;
            }
            return this.mCam;
        }
    }

    public float fov
    {
        get
        {
            return this.cachedCamera.fieldOfView;
        }
        set
        {
            this.cachedCamera.fieldOfView = value;
        }
    }
}

