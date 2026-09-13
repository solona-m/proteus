// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace XivLiveMesh;

/// <summary>
/// The game camera as a projection: world points to screen pixels and screen pixels to world rays.
/// Captured once per frame with <see cref="TryCapture"/>, then used for every vertex.
/// </summary>
public readonly struct ScreenProjection
{
    private readonly Matrix4x4 viewProjection;
    private readonly Vector2 size;

    /// <summary>The camera's position in the world.</summary>
    public Vector3 CameraPosition { get; }

    private ScreenProjection(Matrix4x4 viewProjection, Vector2 size, Vector3 cameraPosition)
    {
        this.viewProjection = viewProjection;
        this.size = size;
        CameraPosition = cameraPosition;
    }

    /// <summary>Read the active camera. False when there is none (loading screens, character select).</summary>
    public static unsafe bool TryCapture(out ScreenProjection projection)
    {
        projection = default;
        var manager = CameraManager.Instance();
        if (manager == null) return false;
        var camera = manager->GetActiveCamera();
        if (camera == null) return false;

        var scene = &camera->CameraBase.SceneCamera;
        if (scene->RenderCamera == null) return false;
        var device = Device.Instance();
        if (device == null || device->Width == 0 || device->Height == 0) return false;

        var view = scene->ViewMatrix;
        view.M44 = 1f;
        Matrix4x4.Invert(view, out var inverseView);
        projection = new ScreenProjection(view * scene->RenderCamera->ProjectionMatrix,
                                          new Vector2(device->Width, device->Height), inverseView.Translation);
        return true;
    }

    /// <summary>
    /// A world point's position in back-buffer pixels. False when the point is behind the camera; a point in
    /// front but off screen still returns its (off-screen) pixel.
    /// </summary>
    public bool WorldToScreen(Vector3 world, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (clip.W <= 1e-6f)
        {
            screen = default;
            return false;
        }
        float inv = 1f / clip.W;
        screen = new Vector2((clip.X * inv + 1f) * 0.5f * size.X, (1f - clip.Y * inv) * 0.5f * size.Y);
        return true;
    }

    /// <summary>The world ray through a back-buffer pixel, from the camera. Uses the game's own unprojection.</summary>
    public static unsafe bool TryScreenRay(Vector2 screen, out Vector3 origin, out Vector3 direction)
    {
        origin = direction = default;
        var manager = CameraManager.Instance();
        if (manager == null) return false;
        var camera = manager->GetActiveCamera();
        if (camera == null) return false;

        var ray = camera->CameraBase.SceneCamera.ScreenPointToRay(screen);
        origin = ray.Origin;
        direction = ray.Direction;
        float len = direction.Length();
        if (len < 1e-6f) return false;
        direction /= len;
        return true;
    }
}
