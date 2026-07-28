using FlaxEngine;

namespace Game.Game.Trials;

/// <summary>
/// 独立下雨控制器: 驱动水面的雨滴强度 (RainStrength)。
/// 作为独立模块挂载到场景即可开启下雨, 移除即停止, 与水体解耦。
/// </summary>
public class RainController : Script
{
    [Tooltip("Base rain intensity (0 = off)")]
    public float Intensity = 0.5f;

    [Tooltip("Enable random gusts (intensity fluctuation)")]
    public bool Gusts = true;

    [Tooltip("Gust fluctuation amount (fraction of intensity)")]
    public float GustAmount = 0.4f;

    [Tooltip("Gust variation speed")]
    public float GustSpeed = 0.7f;

    [Tooltip("Fade in/out time when enabling/disabling (seconds)")]
    public float FadeTime = 1.5f;

    private float _currentStrength;
    private float _fadeTimer;

    public override void OnEnable()
    {
        _fadeTimer = 0f;
    }

    public override void OnUpdate()
    {
        var water = InteractiveWater.Instance;
        if (water == null)
            return;

        // 淡入
        if (_fadeTimer < FadeTime)
            _fadeTimer += Time.DeltaTime;
        float fade = Mathf.Saturate(FadeTime > 0.001f ? _fadeTimer / FadeTime : 1.0f);

        // 基础强度 + 阵风调制 (多频正弦叠加模拟随机起伏)
        float strength = Intensity;
        if (Gusts && Intensity > 0.001f)
        {
            float t = (float)Time.GameTime * GustSpeed;
            float gust = 0.6f * Mathf.Sin(t)
                       + 0.3f * Mathf.Sin(t * 2.3f + 1.7f)
                       + 0.1f * Mathf.Sin(t * 5.1f + 4.2f); // [-1, 1]
            strength *= 1.0f + GustAmount * gust;
            strength = Mathf.Max(strength, 0.0f);
        }

        _currentStrength = strength * fade;
        water.RainStrength = _currentStrength;
    }

    public override void OnDisable()
    {
        // 停止下雨: 归零水面雨强
        var water = InteractiveWater.Instance;
        if (water != null)
            water.RainStrength = 0.0f;
    }
}
