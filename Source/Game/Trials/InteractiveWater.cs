using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FlaxEngine;

namespace Game.Game.Trials;

/// <summary>
/// 模拟质量等级, 决定流体纹理分辨率
/// </summary>
public enum WaterQualityLevel
{
    Low = 512,
    Medium = 1024,
    High = 2048,
    Ultra = 4096,
}

/// <summary>
/// 边界条件模式
/// </summary>
public enum WaterBoundaryMode
{
    Solid = 0, // 固体墙: 波反射
    Open = 1,  // 开放: 边缘吸收
    Wrap = 2,  // 环绕: 周期边界
}

/// <summary>
/// Gerstner 环境浪预设: 自动生成一组自然波浪, 无需手动配置每个波。
/// </summary>
public enum WaterPreset
{
    Custom = 0,     // 使用手动配置的 Waves 数组
    CalmLake,       // 平静湖泊: 小幅缓波
    Ocean,          // 海洋: 中等涌浪
    StormySea,      // 风暴海: 大幅混乱波
    CoastalShore,   // 海岸: 定向拍岸浪
}

/// <summary>
/// Gerstner 环境浪参数 (CPU/GPU 共用同一公式, 保证渲染与浮力一致)。
/// 内存布局 32 字节 (8 float), 与 shader 结构体一致。
/// </summary>
[Serializable]
[StructLayout(LayoutKind.Sequential)]
public struct GerstnerWave
{
    public Float2 Direction;  // 归一化传播方向 (XZ)
    public float Wavelength;  // 波长
    public float Amplitude;   // 振幅
    public float Speed;       // 角频率 (相位移动速度)
    public float Steepness;   // 陡度 (保留)
    public float Phase;       // 初始相位
    [HideInEditor]
    public float Pad;
}

/// <summary>
/// 交互式 2D 流体水面: 基于浅水方程 (SWE) 的 GPU 模拟。
/// 高度场 h 与速度场 (u,v) 耦合, 支持波传播/反射、水流汇聚、多触点交互、泡沫与多尺度法线。
/// </summary>
public class InteractiveWater : PostProcessEffect
{
    // ---------------------------------------------------------
    // 常量
    // ---------------------------------------------------------
    private const int ThreadGroupSize = 8;
    private const float MaxAccumulatedTime = 0.1f; // 防止螺旋死亡
    private const int MaxTouches = 16;             // 多触点上限
    private const int DetailNormalSize = 512;      // 预计算细节法线纹理尺寸
    public const int MaxWaves = 8;                 // Gerstner 环境浪上限

    // ---------------------------------------------------------
    // 流体物理参数 (编辑器可调)
    // ---------------------------------------------------------

    [Tooltip("Gravity acceleration (drives wave propagation)"), Limit(0.1f, 5000.0f, 0.1f)]
    public float Gravity = 9.8f;

    [Tooltip("Mean water depth H"), Limit(0.01f, 100.0f, 0.01f)]
    public float Depth = 1.0f;

    [Tooltip("Viscosity (velocity diffusion)"), Limit(0.0f, 5.0f, 0.01f)]
    public float Viscosity = 0.1f;

    [Tooltip("Linear velocity damping"), Limit(0.0f, 10.0f, 0.01f)]
    public float Drag = 0.3f;

    [Tooltip("Velocity advection strength (0 = off)"), Limit(0.0f, 2.0f, 0.01f)]
    public float AdvectionStrength = 0.0f;

    [Tooltip("Simulation updates per second"), Limit(15f, 120.0f, 1f)]
    public float SimulateSpeed = 60f;

    // ---------------------------------------------------------
    // 法线参数
    // ---------------------------------------------------------

    [Tooltip("Height-field normal strength"), Limit(0.0f, 20.0f, 0.01f)]
    public float NormalStrength = 1.0f;

    [Tooltip("Detail noise layer 1 scale"), Limit(1.0f, 512.0f, 1.0f)]
    public float DetailScale1 = 64.0f;

    [Tooltip("Detail noise layer 1 strength"), Limit(0.0f, 5.0f, 0.01f)]
    public float DetailStrength1 = 0.5f;

    [Tooltip("Detail noise layer 1 flow speed"), Limit(-10.0f, 10.0f, 0.01f)]
    public float DetailSpeed1 = 1.0f;

    [Tooltip("Detail noise layer 2 scale"), Limit(1.0f, 512.0f, 1.0f)]
    public float DetailScale2 = 128.0f;

    [Tooltip("Detail noise layer 2 strength"), Limit(0.0f, 5.0f, 0.01f)]
    public float DetailStrength2 = 0.3f;

    [Tooltip("Detail noise layer 2 flow speed"), Limit(-10.0f, 10.0f, 0.01f)]
    public float DetailSpeed2 = -0.7f;

    // ---------------------------------------------------------
    // 泡沫参数
    // ---------------------------------------------------------

    [Tooltip("Foam generation rate"), Limit(0.0f, 20.0f, 0.01f)]
    public float FoamGeneration = 1.0f;

    [Tooltip("Foam decay rate"), Limit(0.0f, 10.0f, 0.01f)]
    public float FoamDecay = 0.5f;

    [Tooltip("Rain strength (0 = off)"), Limit(0.0f, 5.0f, 0.01f)]
    public float RainStrength = 0.0f;

    [Tooltip("Seconds to keep simulating after last activity before idling"), Limit(0.0f, 30.0f, 0.5f)]
    public float IdleSettleTime = 8.0f;

    // ---------------------------------------------------------
    // 边界与交互
    // ---------------------------------------------------------

    [Tooltip("Boundary condition mode")]
    public WaterBoundaryMode Boundary = WaterBoundaryMode.Solid;

    [Tooltip("Touch impulse radius (in texels)"), Limit(1.0f, 200.0f, 1.0f)]
    public float TouchRadius = 20.0f;

    [Tooltip("Touch impulse strength"), Limit(0.0f, 100.0f, 0.1f)]
    public float TouchStrength = 5.0f;

    [Tooltip("World-space size of the water mesh")]
    public float MeshSize = 500f;

    [Tooltip("Simulation texture resolution")]
    public WaterQualityLevel Quality = WaterQualityLevel.High;

    // ---------------------------------------------------------
    // Gerstner 环境浪 (预设自动生成 或 手动配置)
    // ---------------------------------------------------------

    [Tooltip("Wave preset (auto-generates Waves). Use Custom to edit Waves manually.")]
    public WaterPreset Preset = WaterPreset.Ocean;

    [Tooltip("Gerstner ambient waves (up to 8). Auto-filled by Preset, or edit when Preset=Custom.")]
    public GerstnerWave[] Waves =
    {
        new GerstnerWave { Direction = new Float2(1.0f, 0.2f), Wavelength = 800f, Amplitude = 6f, Speed = 1.2f, Steepness = 0.5f, Phase = 0f },
        new GerstnerWave { Direction = new Float2(-0.4f, 1.0f), Wavelength = 400f, Amplitude = 7f, Speed = 1.8f, Steepness = 0.5f, Phase = 1.3f },
        new GerstnerWave { Direction = new Float2(0.7f, -0.7f), Wavelength = 200f, Amplitude = 4f, Speed = 0.5f, Steepness = 0.5f, Phase = 2.7f },
        new GerstnerWave { Direction = new Float2(0.3f, 0.2f), Wavelength = 700f, Amplitude = 3f, Speed = 0.2f, Steepness = 0.5f, Phase = 3.3f },
        new GerstnerWave { Direction = new Float2(-0.7f, -0.3f), Wavelength = 500f, Amplitude = 7f, Speed = 0.8f, Steepness = 0.5f, Phase = 4.3f },
        new GerstnerWave { Direction = new Float2(0.1f, 0.8f), Wavelength = 300f, Amplitude = 5, Speed = 0.5f, Steepness = 0.5f, Phase = 5.7f },
    };

    // ---------------------------------------------------------
    // 资源引用
    // ---------------------------------------------------------

    public Shader RippleShader;
    public MaterialInstance WaterMaterial;
    public string RippleTextureParam = "Ripple Texture";
    public string NormalTextureParam = "Normal Texture";

    // ---------------------------------------------------------
    // 内部状态
    // ---------------------------------------------------------

    // 常量缓冲数据 (布局必须与 shader 的 SimData 完全一致)
    [StructLayout(LayoutKind.Sequential)]
    private struct SimData
    {
        public float Gravity;
        public float Depth;
        public float Viscosity;
        public float Drag;
        public float AdvectionStrength;
        public float DeltaTime;
        public float TexelSize;
        public float TouchCount;
        public float NormalStrength;
        public float DetailScale1;
        public float DetailStrength1;
        public float DetailSpeed1;
        public float DetailScale2;
        public float DetailStrength2;
        public float DetailSpeed2;
        public float Time;
        public float FoamGeneration;
        public float FoamDecay;
        public float BoundaryMode;
        public float RainStrength;
        public Float2 TouchPosition;
        public float TouchRadius;
        public float TouchStrength;
        public float WaveCount;
        public float MeshSize;
        public float WaterOriginX;
        public float WaterOriginZ;
    }

    private bool _isComputeSupported;
    public GPUTexture _stateA;       // (h, u, v, foam)
    public GPUTexture _stateB;
    public GPUTexture _normalField;
    private GPUTexture _detailNormalTex; // 预计算可平铺细节法线
    private bool _detailNormalGenerated;
    private GPUBuffer _touchBuffer;  // StructuredBuffer<float4> 多触点
    private GPUBuffer _waveBuffer;   // StructuredBuffer<GerstnerWave> 环境浪
    private readonly Float4[] _touchData = new Float4[MaxTouches];
    private readonly GerstnerWave[] _waveData = new GerstnerWave[MaxWaves];
    private WaterPreset _lastPreset;
    private bool _pingPongFlip;
    private float _accumulator;
    private int _textureSize;
    private bool _cflWarned;
    private Float2 _cbTouchPosition;
    private float _cbTouchStrength;
    private double _lastActivityTime;
    private readonly List<Float4> _objectTouches = new List<Float4>();

    /// <summary>全局实例, 供 BuoyantObject 等交互组件访问</summary>
    public static InteractiveWater Instance { get; private set; }

    /// <summary>水面基准高度 (世界 Y, 不含波浪)</summary>
    public float WaterSurfaceY => Actor.Position.Y;

    // ---------------------------------------------------------
    // 属性
    // ---------------------------------------------------------

    public Float2 TouchPosition { get; private set; }
    public int TextureSize => _textureSize;

    // ---------------------------------------------------------
    // 生命周期
    // ---------------------------------------------------------

    public override void OnEnable()
    {
        _textureSize = (int)Quality;
        _pingPongFlip = false;
        _accumulator = 0f;
        _cflWarned = false;
        _lastActivityTime = Time.GameTime;

        // 状态纹理: RGBA16F 承载 (h, u, v, foam), Ping-Pong 双缓冲
        _stateA = CreateStateTexture(_textureSize);
        _stateB = CreateStateTexture(_textureSize);

        // 多尺度法线输出纹理
        _normalField = new GPUTexture();
        GPUTextureDescription normalDesc = GPUTextureDescription.New2D(
            _textureSize,
            _textureSize,
            PixelFormat.R16G16B16A16_Float,
            GPUTextureFlags.UnorderedAccess | GPUTextureFlags.ShaderResource
        );
        _normalField.Init(ref normalDesc);

        // 预计算可平铺细节法线纹理 (启动时生成一次, 替代每帧 FBM)
        _detailNormalTex = new GPUTexture();
        GPUTextureDescription detailDesc = GPUTextureDescription.New2D(
            DetailNormalSize,
            DetailNormalSize,
            PixelFormat.R11G11B10_Float,
            GPUTextureFlags.UnorderedAccess | GPUTextureFlags.ShaderResource
        );
        _detailNormalTex.Init(ref detailDesc);
        _detailNormalGenerated = false;

        // 多触点结构化缓冲 (动态, 每帧 CPU 更新)
        _touchBuffer = new GPUBuffer();
        GPUBufferDescription touchDesc = GPUBufferDescription.Structured(MaxTouches, 16, false);
        touchDesc.Usage = GPUResourceUsage.Dynamic;
        _touchBuffer.Init(ref touchDesc);

        // Gerstner 环境浪结构化缓冲 (32 字节/波)
        _waveBuffer = new GPUBuffer();
        GPUBufferDescription waveDesc = GPUBufferDescription.Structured(MaxWaves, 32, false);
        waveDesc.Usage = GPUResourceUsage.Dynamic;
        _waveBuffer.Init(ref waveDesc);

        // 绑定材质参数
        if (WaterMaterial)
        {
            WaterMaterial.SetParameterValue(RippleTextureParam, _stateA);
            WaterMaterial.SetParameterValue(NormalTextureParam, _normalField);
        }

        _isComputeSupported = GPUDevice.Instance.Limits.HasCompute;
        MainRenderTask.Instance.AddCustomPostFx(this);

        Instance = this;

        // 应用波浪预设 (非 Custom 时自动生成 Waves + 细节噪声参数)
        _lastPreset = Preset;
        if (Preset != WaterPreset.Custom)
            ApplyPreset(Preset);

        ValidateCFL();
    }

    public override void OnDisable()
    {
        if (WaterMaterial)
        {
            WaterMaterial.SetParameterValue(RippleTextureParam, null);
            WaterMaterial.SetParameterValue(NormalTextureParam, null);
        }

        MainRenderTask.Instance?.RemoveCustomPostFx(this);
        if (Instance == this)
            Instance = null;
        ReleaseBuffers();
    }

    public override void OnUpdate()
    {
        // 预设切换时重新生成波浪 + 细节噪声参数 (Custom 保留手动编辑)
        if (Preset != _lastPreset)
        {
            _lastPreset = Preset;
            if (Preset != WaterPreset.Custom)
                ApplyPreset(Preset);
        }
    }

    public override bool CanRender()
    {
        return base.CanRender()
            && _isComputeSupported
            && RippleShader != null && RippleShader.IsLoaded
            && _stateA != null && _stateB != null && _normalField != null;
    }

    // ---------------------------------------------------------
    // 渲染 (固定步长 + Compute Dispatch)
    // ---------------------------------------------------------

    public override unsafe void Render(
        GPUContext context,
        ref RenderContext renderContext,
        GPUTexture input,
        GPUTexture output
    )
    {
        // 收集本帧触点 (鼠标 + 物体入水)
        int touchCount = CollectTouches();

        // 活跃度跟踪: 有触点或下雨时视为活跃
        bool hasActivity = touchCount > 0 || RainStrength > 0.001f;
        if (hasActivity)
            _lastActivityTime = FlaxEngine.Time.GameTime;
        bool isActive = (FlaxEngine.Time.GameTime - _lastActivityTime) < IdleSettleTime;

        // 固定时间步长累加器
        float frameTime = Mathf.Min(Time.DeltaTime, MaxAccumulatedTime);
        _accumulator += frameTime;

        float fixedStep = 1.0f / SimulateSpeed;
        bool simulated = false;
        bool firstStep = true;

        // 静止时跳过模拟 (水面已平息), 节省 GPU; 法线 Pass 仍运行以保持细节流动
        if (isActive)
        {
            while (_accumulator >= fixedStep)
            {
                // 触点冲量仅在首个子步施加一次, 保证帧率无关
                DispatchSimulation(context, fixedStep, firstStep ? touchCount : 0);
                firstStep = false;
                _accumulator -= fixedStep;
                simulated = true;

                if (_accumulator > fixedStep * 3)
                {
                    _accumulator = 0;
                    break;
                }
            }
        }
        else
        {
            _accumulator = 0f;
        }

        // 法线 Pass 每帧都运行 (静止时保持细节噪声流动)
        DispatchNormals(context);

        // 更新材质引用的最新状态纹理
        GPUTexture readTexture = _pingPongFlip ? _stateB : _stateA;
        if (WaterMaterial)
            WaterMaterial.SetParameterValue(RippleTextureParam, readTexture);
    }

    // ---------------------------------------------------------
    // 内部方法
    // ---------------------------------------------------------

    /// <summary>
    /// 收集触点 (纹理空间)。返回触点数量; 同时填充 _touchData 与 CB 单触点回退字段。
    /// </summary>
    private int CollectTouches()
    {
        int count = 0;
        TouchPosition = Float2.Zero;
        float cbStrength = 0f;

        if (Input.GetMouseButton(MouseButton.Left))
        {
            var mainCam = Camera.MainCamera;
            if (mainCam != null)
            {
                var ray = mainCam.ConvertMouseToRay(Input.MousePosition);
                if (Physics.RayCast(ray.Position, ray.Direction, out var hitInfo))
                {
                    var worldToUV = new Float2(
                        hitInfo.Point.X + MeshSize * 0.5f - Actor.Position.X,
                        -hitInfo.Point.Z + MeshSize * 0.5f + Actor.Position.Z
                    );
                    Float2 texelPos = worldToUV / MeshSize * _textureSize;
                    TouchPosition = texelPos;
                    cbStrength = TouchStrength;

                    if (count < MaxTouches)
                    {
                        _touchData[count] = new Float4(texelPos.X, texelPos.Y, TouchStrength, TouchRadius);
                        count++;
                    }
                }
            }
        }

        // CB 单触点回退字段 (当结构化缓冲不可用 / TouchCount==0 时使用)
        _cbTouchPosition = TouchPosition;
        _cbTouchStrength = cbStrength;

        // 收集物体交互触点 (BuoyantObject 等本帧注入的冲量)
        for (int i = 0; i < _objectTouches.Count && count < MaxTouches; i++)
        {
            _touchData[count] = _objectTouches[i];
            count++;
        }
        _objectTouches.Clear();

        // 若结构化缓冲不可用, 返回 0 让 shader 走 CB 回退路径
        return _touchBuffer != null ? count : 0;
    }

    /// <summary>
    /// 由交互组件调用: 注入一个物体触点 (世界坐标自动换算为纹理坐标)。
    /// </summary>
    /// <param name="worldPos">物体世界位置</param>
    /// <param name="strength">冲量强度</param>
    /// <param name="worldRadius">影响半径 (世界单位)</param>
    public void AddObjectTouch(Vector3 worldPos, float strength, float worldRadius)
    {
        if (strength <= 0.001f)
            return;
        var worldToUV = new Float2(
            worldPos.X + MeshSize * 0.5f - Actor.Position.X,
            -worldPos.Z + MeshSize * 0.5f + Actor.Position.Z
        );
        Float2 texelPos = worldToUV / MeshSize * _textureSize;
        float texelRadius = Mathf.Max(worldRadius / MeshSize * _textureSize, 1.0f);
        _objectTouches.Add(new Float4(texelPos.X, texelPos.Y, strength, texelRadius));
    }

    private SimData BuildSimData(float dt, int touchCount)
    {
        return new SimData
        {
            Gravity = Gravity,
            Depth = Depth,
            Viscosity = Viscosity,
            Drag = Drag,
            AdvectionStrength = AdvectionStrength,
            DeltaTime = dt,
            TexelSize = 1.0f / _textureSize,
            TouchCount = touchCount,
            NormalStrength = NormalStrength,
            DetailScale1 = DetailScale1,
            DetailStrength1 = DetailStrength1,
            DetailSpeed1 = DetailSpeed1,
            DetailScale2 = DetailScale2,
            DetailStrength2 = DetailStrength2,
            DetailSpeed2 = DetailSpeed2,
            Time = (float)FlaxEngine.Time.GameTime,
            FoamGeneration = FoamGeneration,
            FoamDecay = FoamDecay,
            BoundaryMode = (float)Boundary,
            RainStrength = RainStrength,
            TouchPosition = _cbTouchPosition,
            TouchRadius = TouchRadius,
            TouchStrength = _cbTouchStrength,
            WaveCount = Math.Min(Waves?.Length ?? 0, MaxWaves),
            MeshSize = MeshSize,
            WaterOriginX = Actor.Position.X,
            WaterOriginZ = Actor.Position.Z,
        };
    }

    private unsafe void DispatchSimulation(GPUContext context, float dt, int touchCount)
    {
        GPUTexture srcTexture = _pingPongFlip ? _stateB : _stateA;
        GPUTexture dstTexture = _pingPongFlip ? _stateA : _stateB;

        var cb = RippleShader.GPU.GetCB(0);
        if (cb == IntPtr.Zero)
            return;

        SimData data = BuildSimData(dt, touchCount);
        context.UpdateCB(cb, new IntPtr(&data));
        context.BindCB(0, cb);

        // 更新并绑定多触点缓冲 (t1)
        if (_touchBuffer != null)
        {
            if (touchCount > 0)
            {
                fixed (Float4* ptr = _touchData)
                {
                    context.UpdateBuffer(_touchBuffer, new IntPtr(ptr), (uint)(touchCount * 16));
                }
            }
            context.BindSR(1, _touchBuffer.View());
        }

        uint groupCount = (uint)(_textureSize / ThreadGroupSize);

        // ---- Pass 1: SWE 流体模拟 ----
        context.BindSR(0, srcTexture);
        context.BindUA(0, dstTexture.View());
        var csSimulate = RippleShader.GPU.GetCS("CS_Simulate");
        context.Dispatch(csSimulate, groupCount, groupCount, 1);
        context.ResetUA();
        context.ResetSR();
        context.ResetCB();

        _pingPongFlip = !_pingPongFlip;
    }

    /// <summary>
    /// 法线 Pass: 从最新状态合成多尺度法线。每帧运行 (静止时保持细节流动)。
    /// </summary>
    private unsafe void DispatchNormals(GPUContext context)
    {
        GPUTexture latestState = _pingPongFlip ? _stateB : _stateA;

        var cb = RippleShader.GPU.GetCB(0);
        if (cb == IntPtr.Zero)
            return;

        SimData data = BuildSimData(1.0f / SimulateSpeed, 0);
        context.UpdateCB(cb, new IntPtr(&data));
        context.BindCB(0, cb);

        // 绑定触点缓冲 (t1) 避免未绑定
        if (_touchBuffer != null)
            context.BindSR(1, _touchBuffer.View());

        // 首帧生成可平铺细节法线纹理 (仅需一次)
        if (!_detailNormalGenerated)
        {
            context.BindUA(2, _detailNormalTex.View());
            var csGenDetail = RippleShader.GPU.GetCS("CS_GenerateDetailNormal");
            uint detailGroups = (uint)(DetailNormalSize / ThreadGroupSize);
            context.Dispatch(csGenDetail, detailGroups, detailGroups, 1);
            context.ResetUA();
            _detailNormalGenerated = true;
        }

        // 绑定预计算细节法线纹理 (t2)
        context.BindSR(2, _detailNormalTex);

        // 填充并绑定 Gerstner 环境浪缓冲 (t3)
        UpdateWaveBuffer(context);

        uint groupCount = (uint)(_textureSize / ThreadGroupSize);

        // ---- Pass 2: 多尺度法线合成 ----
        context.BindSR(0, latestState);
        context.BindUA(1, _normalField.View());
        var csNormals = RippleShader.GPU.GetCS("CS_ComputeNormals");
        context.Dispatch(csNormals, groupCount, groupCount, 1);
        context.ResetUA();
        context.ResetSR();
        context.ResetCB();
    }

    /// <summary>
    /// 填充并绑定 Gerstner 波缓冲 (归一化方向)。
    /// </summary>
    private unsafe void UpdateWaveBuffer(GPUContext context)
    {
        if (_waveBuffer == null)
            return;

        int waveCount = Math.Min(Waves?.Length ?? 0, MaxWaves);
        if (waveCount > 0)
        {
            for (int i = 0; i < waveCount; i++)
            {
                GerstnerWave w = Waves[i];
                Float2 dir = w.Direction;
                float len = dir.Length;
                if (len > 0.0001f)
                    dir = dir / len;
                else
                    dir = new Float2(1.0f, 0.0f);
                _waveData[i] = new GerstnerWave
                {
                    Direction = dir,
                    Wavelength = w.Wavelength,
                    Amplitude = w.Amplitude,
                    Speed = w.Speed,
                    Steepness = w.Steepness,
                    Phase = w.Phase,
                    Pad = 0f,
                };
            }
            fixed (GerstnerWave* ptr = _waveData)
            {
                context.UpdateBuffer(_waveBuffer, new IntPtr(ptr), (uint)(waveCount * 32));
            }
        }
        context.BindSR(3, _waveBuffer.View());
    }

    /// <summary>
    /// 计算指定世界位置的水面高度 (基准高度 + Gerstner 环境浪)。
    /// 与 shader 使用同一公式, 供浮力等 CPU 逻辑使用。
    /// </summary>
    public float GetWaterHeight(Vector3 worldPos)
    {
        float height = WaterSurfaceY;
        if (Waves == null)
            return height;

        float time = (float)FlaxEngine.Time.GameTime;
        Float2 posxz = new Float2(worldPos.X, worldPos.Z);
        int waveCount = Math.Min(Waves.Length, MaxWaves);
        for (int i = 0; i < waveCount; i++)
        {
            GerstnerWave w = Waves[i];
            if (w.Amplitude <= 0.0001f || w.Wavelength <= 0.0001f)
                continue;
            Float2 dir = w.Direction;
            float len = dir.Length;
            if (len > 0.0001f)
                dir = dir / len;
            else
                dir = new Float2(1.0f, 0.0f);

            float k = (float)(2.0 * Math.PI) / w.Wavelength;
            float phase = k * Float2.Dot(dir, posxz) + w.Phase - w.Speed * time;
            height += w.Amplitude * Mathf.Sin(phase);
        }
        return height;
    }

    /// <summary>
    /// 根据预设自动生成一组自然的 Gerstner 波 (仿频谱分布: 长波为主, 短波递减, 方向围绕主方向扩散)。
    /// </summary>
    public static GerstnerWave[] GenerateWaves(WaterPreset preset)
    {
        int count;
        float baseAmp, minWL, maxWL, domX, domY, spread, speedBase, falloff;
        switch (preset)
        {
            case WaterPreset.CalmLake:
                count = 4; baseAmp = 2.5f; minWL = 300f; maxWL = 700f;
                domX = 1f; domY = 0.2f; spread = 3.14f; speedBase = 0.4f; falloff = 0.7f;
                break;
            case WaterPreset.Ocean:
                count = 6; baseAmp = 6f; minWL = 300f; maxWL = 1200f;
                domX = 1f; domY = 0.3f; spread = 1.2f; speedBase = 0.9f; falloff = 0.75f;
                break;
            case WaterPreset.StormySea:
                count = 8; baseAmp = 12f; minWL = 150f; maxWL = 900f;
                domX = 1f; domY = 0f; spread = 3.14f; speedBase = 1.6f; falloff = 0.82f;
                break;
            case WaterPreset.CoastalShore:
                count = 5; baseAmp = 5.5f; minWL = 250f; maxWL = 900f;
                domX = 1f; domY = 0f; spread = 0.5f; speedBase = 1.1f; falloff = 0.7f;
                break;
            default:
                return new GerstnerWave[0];
        }

        var waves = new GerstnerWave[count];
        float domAngle = Mathf.Atan2(domY, domX);
        for (int i = 0; i < count; i++)
        {
            float t = count > 1 ? (float)i / (count - 1) : 0f;
            // 波长: 长波 → 短波
            float wavelength = Mathf.Lerp(maxWL, minWL, t);
            // 振幅: 随序号递减 (仿频谱衰减)
            float amplitude = baseAmp * Mathf.Pow(falloff, i);
            // 方向: 主方向 + 确定性随机偏移
            float angleOffset = (Hash01(i * 2 + 1) - 0.5f) * 2f * spread;
            float angle = domAngle + angleOffset;
            var dir = new Float2(Mathf.Cos(angle), Mathf.Sin(angle));
            // 速度: 与波长相关 (深水色散: 长波更快) + 随机拖动
            float speed = speedBase * Mathf.Sqrt(wavelength / maxWL) * (0.8f + 0.4f * Hash01(i * 3 + 2));
            float phase = Hash01(i * 5 + 3) * (float)(2.0 * Math.PI);
            waves[i] = new GerstnerWave
            {
                Direction = dir,
                Wavelength = wavelength,
                Amplitude = amplitude,
                Speed = speed,
                Steepness = 0.5f,
                Phase = phase,
                Pad = 0f,
            };
        }
        return waves;
    }

    /// <summary>确定性哈希 → [0,1] (用于预设生成的随机但可复现的波参数)。</summary>
    private static float Hash01(int n)
    {
        uint x = (uint)n * 2654435761u;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / (float)0x1000000;
    }

    /// <summary>
    /// 应用预设: 统一配置 Gerstner 波浪 与 细节噪声参数 (尺度/强度/速度)。
    /// </summary>
    public void ApplyPreset(WaterPreset preset)
    {
        Waves = GenerateWaves(preset);
        switch (preset)
        {
            case WaterPreset.CalmLake: // 平静: 微弱细节, 缓慢流动
                DetailScale1 = 64f; DetailStrength1 = 0.15f; DetailSpeed1 = 0.3f;
                DetailScale2 = 128f; DetailStrength2 = 0.1f; DetailSpeed2 = -0.2f;
                break;
            case WaterPreset.Ocean: // 海洋: 中等细节
                DetailScale1 = 64f; DetailStrength1 = 0.3f; DetailSpeed1 = 0.8f;
                DetailScale2 = 128f; DetailStrength2 = 0.2f; DetailSpeed2 = -0.6f;
                break;
            case WaterPreset.StormySea: // 风暴: 强细节, 快速流动
                DetailScale1 = 80f; DetailStrength1 = 0.5f; DetailSpeed1 = 1.8f;
                DetailScale2 = 160f; DetailStrength2 = 0.4f; DetailSpeed2 = -1.4f;
                break;
            case WaterPreset.CoastalShore: // 海岸: 中等偏强细节
                DetailScale1 = 64f; DetailStrength1 = 0.35f; DetailSpeed1 = 1.0f;
                DetailScale2 = 128f; DetailStrength2 = 0.25f; DetailSpeed2 = -0.8f;
                break;
        }
    }

    /// <summary>
    /// CFL 稳定性校验: 要求 dt * sqrt(g*H) / dx 小于 1。违反时警告 (可能导致发散)。
    /// </summary>
    private void ValidateCFL()
    {
        float dt = 1.0f / SimulateSpeed;
        float waveSpeed = Mathf.Sqrt(Mathf.Max(Gravity * Depth, 0f));
        float cfl = dt * waveSpeed; // dx = 1 texel
        if (cfl >= 1.0f && !_cflWarned)
        {
            Debug.LogWarning(
                $"[InteractiveWater] CFL 条件违反 (dt*sqrt(g*H) = {cfl:F3} >= 1), 模拟可能发散。" +
                $"请降低 Gravity/Depth 或提高 SimulateSpeed。");
            _cflWarned = true;
        }
    }

    private static GPUTexture CreateStateTexture(int size)
    {
        var texture = new GPUTexture();
        GPUTextureDescription desc = GPUTextureDescription.New2D(
            size,
            size,
            PixelFormat.R16G16B16A16_Float,
            GPUTextureFlags.UnorderedAccess | GPUTextureFlags.ShaderResource
        );
        texture.Init(ref desc);
        return texture;
    }

    private void ReleaseBuffers()
    {
        if (_stateA)
        {
            _stateA.ReleaseGPU();
            Destroy(ref _stateA);
        }
        if (_stateB)
        {
            _stateB.ReleaseGPU();
            Destroy(ref _stateB);
        }
        if (_normalField)
        {
            _normalField.ReleaseGPU();
            Destroy(ref _normalField);
        }
        if (_detailNormalTex)
        {
            _detailNormalTex.ReleaseGPU();
            Destroy(ref _detailNormalTex);
        }
        if (_touchBuffer != null)
        {
            _touchBuffer.ReleaseGPU();
            Destroy(ref _touchBuffer);
        }
        if (_waveBuffer != null)
        {
            _waveBuffer.ReleaseGPU();
            Destroy(ref _waveBuffer);
        }
    }
}
