using System;
using FlaxEngine;

namespace Game.Game.Trials;

/// <summary>
/// 反射/折射渲染工具类: 提供反射相机计算和斜裁剪投影矩阵
/// </summary>
public static class ReflectionUtils
{
    /// <summary>
    /// 根据源相机 Transform 和反射平面计算反射相机的 Transform
    /// </summary>
    /// <param name="sourceTransform">源相机 Transform</param>
    /// <param name="planePosition">反射平面上的点</param>
    /// <param name="planeNormal">反射平面法线 (朝上)</param>
    /// <returns>反射相机的 Transform</returns>
    public static Transform CalculateReflectionTransform(
        Transform sourceTransform, Vector3 planePosition, Vector3 planeNormal)
    {
        float d = -Vector3.Dot(planeNormal, planePosition);

        // 反射相机位置
        Vector3 camPos = sourceTransform.Translation;
        Vector3 reflectedPos = camPos - 2.0f * (Vector3.Dot(camPos, planeNormal) + d) * planeNormal;

        // 反射相机朝向
        Vector3 originalForward = sourceTransform.Forward;
        Vector3 originalUp = sourceTransform.Up;
        Vector3 reflectedForward =
            originalForward - 2.0f * Vector3.Dot(originalForward, planeNormal) * planeNormal;
        Vector3 reflectedUp =
            originalUp - 2.0f * Vector3.Dot(originalUp, planeNormal) * planeNormal;

        return new Transform
        {
            Orientation = Quaternion.LookRotation(reflectedForward, reflectedUp),
            Translation = reflectedPos,
        };
    }

    /// <summary>
    /// 计算斜裁剪投影矩阵 (Eric Lengyel 算法, 适配 Flax 行向量约定与 [0,1] 深度范围)
    /// 将投影矩阵的近裁剪面替换为任意裁剪平面, 用于平面反射/折射的裁剪
    /// </summary>
    /// <param name="projectionMatrix">原始投影矩阵</param>
    /// <param name="viewSpaceClipPlane">视图空间中的裁剪平面 (xyz=法线指向保留侧, w=距离)</param>
    /// <returns>修改后的斜投影矩阵</returns>
    public static Matrix GetObliqueProjectionMatrix(Matrix projectionMatrix, Vector4 viewSpaceClipPlane)
    {
        // Lengyel 算法: q = sgn(C) * proj^(-1) (行向量约定)
        // q 是视锥体中距裁剪平面最远的角点 (齐次坐标)
        Matrix invProj = Matrix.Invert(projectionMatrix);

        Vector4 signVector = new(
            Math.Sign(viewSpaceClipPlane.X),
            Math.Sign(viewSpaceClipPlane.Y),
            1.0f,
            1.0f
        );

        Vector4 q = Vector4.Transform(signVector, invProj);

        // 计算缩放因子: Flax 使用 [0,1] 深度范围, 故系数为 1 而非 2
        float dot = Vector4.Dot(viewSpaceClipPlane, q);
        if (Math.Abs(dot) < 1e-6f)
            return projectionMatrix; // 退化情况, 返回原始矩阵

        Vector4 c = viewSpaceClipPlane * (1.0f / dot);

        // 替换第三列 (行向量约定下, clip.z 由第三列计算)
        // 使裁剪平面映射到 NDC z=0 (近裁剪面), 远平面保持不变
        // 注意: 不能替换 Column3 (clip.w 列), 否则会全局改变透视除法导致远平面裁剪异常
        Matrix obliqueProj = projectionMatrix;
        obliqueProj.Column3 = c;

        return obliqueProj;
    }
}
