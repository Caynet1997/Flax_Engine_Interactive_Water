using System;
using FlaxEngine;

[RequireActor(typeof(StaticModel))]
public class DynamicTextureFromCode : Script
{
    private GPUTexture _tempTexture;
    private MaterialInstance _tempMaterialInstance;
    private byte[] _data;

    public Material Material;

    public override void OnStart()
    {
        // Create new GPU texture
        var texture = new GPUTexture();
        _tempTexture = texture;
        var desc = GPUTextureDescription.New2D(
            64,
            64,
            PixelFormat.R8G8B8A8_UNorm,
            GPUTextureFlags.ShaderResource
        );
        if (texture.Init(ref desc))
            return;

        // Use a dynamic material instance with a texture to sample
        var material = Material.CreateVirtualInstance();
        _tempMaterialInstance = material;
        material.SetParameterValue("tex", texture);

        // Add a model actor and use the dynamic material for rendering
        var staticModel = Actor as StaticModel;
        staticModel.SetMaterial(0, material);

        // Plug into rendering to update texture at runtime
        MainRenderTask.Instance.PreRender += OnPreRender;
    }

    public override void OnDestroy()
    {
        MainRenderTask.Instance.PreRender -= OnPreRender;

        // Ensure to cleanup resources
        _tempTexture?.ReleaseGPU();
        Destroy(ref _tempTexture);
        Destroy(ref _tempMaterialInstance);
    }

    private unsafe void OnPreRender(GPUContext context, ref RenderContext renderContext)
    {
        if (!Enabled || !Actor.IsActiveInHierarchy)
            return;

        var desc = _tempTexture.Description;
        var size = desc.Width * desc.Height * PixelFormatExtensions.SizeInBytes(desc.Format);
        if (_data == null || _data.Length != size)
            _data = new byte[size];
        fixed (byte* dataPtr = _data)
        {
            // Generate pixels data (linear gradient)
            var colorsPtr = (Color32*)dataPtr;
            var offset = Mathf.Cos(Time.GameTime * 3.0f) * 0.5f + 0.5f;
            for (int y = 0; y < desc.Height; y++)
            {
                float t1 = (float)y / desc.Height;
                var c1 = Color32.Lerp(new Color32((byte)(offset * 255), 0, 0, 1), Color.Blue, t1);
                var c2 = Color32.Lerp(
                    Color.Yellow,
                    new Color32(0, (byte)(144 - offset * 80), 0, 1),
                    t1
                );
                for (int x = 0; x < desc.Width; x++)
                {
                    float t2 = (float)x / desc.Width;
                    colorsPtr[y * desc.Width + x] = Color32.Lerp(c1, c2, t2);
                }
            }

            // Update texture data on a GPU (send data)
            uint rowPitch = (uint)size / (uint)desc.Height;
            uint slicePitch = (uint)size;
            context.UpdateTexture(_tempTexture, 0, 0, new IntPtr(dataPtr), rowPitch, slicePitch);
            _tempTexture.ResidentMipLevels = 1; // Mark mip-map as available (required for standard textures only - other than render textures)
        }
    }
}
