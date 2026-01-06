using System;
using UnityEditor;
using UnityEngine;

namespace Editors.MoveBuilder
{
    public sealed class MoveBuilderPreviewView : IDisposable
    {
        private PreviewRenderUtility _preview;
        private GameObject _previewGO;
        private int _lastPrefabId;

        private Vector2 _panPx;
        private float _zoom = 1f;

        private bool _dragging;
        private int _dragIndex = -1;
        private Vector2 _dragStartMouse;
        private Vector2 _dragStartCenterLocal;

        private enum ResizeHandle
        {
            None,
            N, S, E, W,
            NE, NW, SE, SW
        }

        private bool _resizing;
        private ResizeHandle _activeHandle = ResizeHandle.None;
        private Vector2 _resizeStartMouse;
        private BoxData _resizeStartBox;

        private bool _animationMode;

        public void Dispose()
        {
            ResetPreviewObjects();
            if (_animationMode)
            {
                AnimationMode.StopAnimationMode();
                _animationMode = false;
            }
            _preview?.Cleanup();
            _preview = null;
        }

        public void ResetPreviewObjects()
        {
            if (_previewGO)
            {
                UnityEngine.Object.DestroyImmediate(_previewGO);
                _previewGO = null;
                _lastPrefabId = 0;
            }
        }

        public void Draw(Rect rect, MoveBuilderModel model, int tps)
        {
            EnsurePreviewUtility();

            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f, 1f));

            if (model == null || model.CharacterPrefab == null)
            {
                DrawCentered(rect, "Assign Character Prefab");
                return;
            }

            SyncPrefabFromModel(model);

            if (_previewGO == null)
            {
                DrawCentered(rect, "Preview instance not available");
                return;
            }
            bool clickConsumed = false;

            HandleViewInput(rect, ref clickConsumed);

            ConfigureCamera(rect);

            SampleAnimation(model, tps);

            Texture tex = Render(rect, model);
            if (tex != null)
                GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);

            DrawHitboxOverlay(rect, model, ref clickConsumed);

            HandleBackgroundDeselection(rect, model, ref clickConsumed);
        }

        private void EnsurePreviewUtility()
        {
            if (_preview != null) return;

            _preview = new PreviewRenderUtility();
            _preview.ambientColor = new Color(0.25f, 0.25f, 0.25f, 1f);

            var cam = _preview.camera;
            cam.orthographic = true;
            cam.nearClipPlane = -50f;
            cam.farClipPlane = 50f;
            cam.transform.position = new Vector3(0, 0, -10f);
            cam.transform.rotation = Quaternion.identity;
        }

        private void SyncPrefabFromModel(MoveBuilderModel model)
        {
            // No injection: the Model never sees the preview GO.
            // The view recreates its preview GO whenever the Model’s prefab reference changes.
            int prefabId = model.CharacterPrefab.GetInstanceID();
            if (_previewGO != null && prefabId == _lastPrefabId) return;

            ResetPreviewObjects();
            _lastPrefabId = prefabId;

            _previewGO = _preview.InstantiatePrefabInScene(model.CharacterPrefab);
            _previewGO.hideFlags = HideFlags.HideAndDontSave;
            _previewGO.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _previewGO.transform.localScale = Vector3.one;
        }

        private void HandleViewInput(Rect rect, ref bool clickConsumed)
        {
            var e = Event.current;
            if (e == null) return;

            // Always end drag on mouse up (even if outside the rect).
            if ((_dragging || _resizing) && e.type == EventType.MouseUp && e.button == 0)
            {
                _dragging = false;
                _dragIndex = -1;

                _resizing = false;
                _activeHandle = ResizeHandle.None;
            }

            if (!rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.ScrollWheel)
            {
                float factor = 1f + (-e.delta.y * 0.05f);
                _zoom = Mathf.Clamp(_zoom * factor, 0.2f, 8f);
                e.Use();
            }

            bool panGesture = (e.button == 2) || (e.alt && e.button == 0);
            if (panGesture && e.type == EventType.MouseDrag)
            {
                _panPx += e.delta;
                e.Use();
            }
        }

        private void ConfigureCamera(Rect rect)
        {
            var cam = _preview.camera;

            cam.orthographicSize = 2.5f / Mathf.Max(0.0001f, _zoom);

            float worldPerPixel = (cam.orthographicSize * 2f) / Mathf.Max(1f, rect.height);
            Vector2 panWorld = new Vector2(_panPx.x * worldPerPixel, -_panPx.y * worldPerPixel);

            cam.transform.position = new Vector3(-panWorld.x, -panWorld.y, -10f);
            cam.transform.rotation = Quaternion.identity;
        }

        private void SampleAnimation(MoveBuilderModel model, int tps)
        {
            if (!model.Clip) return;
            if (!_animationMode)
            {
                AnimationMode.StartAnimationMode();
                _animationMode = true;
            }

            float time = model.CurrentTick / (float)Mathf.Max(1, tps);
            time = Mathf.Clamp(time, 0f, Mathf.Max(0f, model.Clip.length - 0.00001f));

            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(_previewGO, model.Clip, time);
            AnimationMode.EndSampling();
        }

        private Texture Render(Rect rect, MoveBuilderModel model)
        {
            _preview.BeginPreview(rect, GUIStyle.none);

            if (model.AllowSrp)
                _preview.Render(allowScriptableRenderPipeline: true);
            else
                _preview.Render();

            return _preview.EndPreview();
        }

        private void DrawHitboxOverlay(Rect rect, MoveBuilderModel model, ref bool clickConsumed)
        {
            var frame = model.GetCurrentFrame();
            if (frame == null) return;

            Transform root = _previewGO.transform;
            Camera cam = _preview.camera;

            Handles.BeginGUI();

            for (int i = 0; i < frame.Boxes.Count; i++)
            {
                var box = frame.Boxes[i];
                Rect guiRect = LocalBoxToGuiRect(rect, cam, root, box);

                float thickness = (i == model.SelectedBoxIndex) ? 2f : 1f;
                Color color = box.Props.Kind == HitboxKind.Hurtbox ? Color.blue : Color.red;
                DrawRectOutline(guiRect, thickness, color);

                GUI.Label(new Rect(guiRect.xMin, guiRect.yMin - 16, 180, 16),
                    $"{i}:{box.Props.Kind}", EditorStyles.whiteMiniLabel);

                HandleBoxDrag(rect, model, frame, i, guiRect, root, cam, ref clickConsumed);
                if (model.ShowResizeHandles && i == model.SelectedBoxIndex)
                {
                    HandleResizeHandles(rect, model, i, guiRect, root, cam, ref clickConsumed);
                }
            }

            Handles.EndGUI();
        }

        private void HandleBoxDrag(
            Rect rect,
            MoveBuilderModel model,
            FrameData frame,
            int index,
            Rect guiRect,
            Transform root,
            Camera cam,
            ref bool clickConsumed)
        {
            var e = Event.current;
            if (e == null) return;
            if (!rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.MouseDown && e.button == 0 && guiRect.Contains(e.mousePosition) && !clickConsumed)
            {
                model.SelectBox(index);

                _dragging = true;
                _dragIndex = index;
                _dragStartMouse = e.mousePosition;
                _dragStartCenterLocal = frame.Boxes[index].CenterLocal;

                e.Use();
                clickConsumed = true;
                return;
            }

            if (_dragging && _dragIndex == index && e.type == EventType.MouseDrag && e.button == 0)
            {
                Vector2 deltaPx = e.mousePosition - _dragStartMouse;

                // Convert GUI pixel delta to local-space delta using orthographic camera scale.
                float worldPerPixel = (cam.orthographicSize * 2f) / Mathf.Max(1f, rect.height);
                Vector2 deltaWorld = new Vector2(deltaPx.x * worldPerPixel, -deltaPx.y * worldPerPixel);

                Vector3 localDelta3 = root.InverseTransformVector(new Vector3(deltaWorld.x, deltaWorld.y, 0f));
                Vector2 localDelta = new Vector2(localDelta3.x, localDelta3.y);

                model.MoveBoxCenter(index, _dragStartCenterLocal + localDelta);

                e.Use();
            }
        }

        private void HandleResizeHandles(
            Rect rect,
            MoveBuilderModel model,
            int index,
            Rect guiRect,
            Transform root,
            Camera cam,
            ref bool clickConsumed)
        {
            var e = Event.current;
            if (e == null) return;

            // We compute handle GUI rects from the current box GUI rect.
            const float size = 8f;
            Rect rN = HandleRect(guiRect.center.x, guiRect.yMin, size);
            Rect rS = HandleRect(guiRect.center.x, guiRect.yMax, size);
            Rect rE = HandleRect(guiRect.xMax, guiRect.center.y, size);
            Rect rW = HandleRect(guiRect.xMin, guiRect.center.y, size);

            Rect rNE = HandleRect(guiRect.xMax, guiRect.yMin, size);
            Rect rNW = HandleRect(guiRect.xMin, guiRect.yMin, size);
            Rect rSE = HandleRect(guiRect.xMax, guiRect.yMax, size);
            Rect rSW = HandleRect(guiRect.xMin, guiRect.yMax, size);

            // Draw handles
            DrawHandle(rN); DrawHandle(rS); DrawHandle(rE); DrawHandle(rW);
            DrawHandle(rNE); DrawHandle(rNW); DrawHandle(rSE); DrawHandle(rSW);

            // Mouse down: pick active handle
            if (!_resizing && e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition) && !clickConsumed)
            {
                ResizeHandle picked =
                    rNE.Contains(e.mousePosition) ? ResizeHandle.NE :
                    rNW.Contains(e.mousePosition) ? ResizeHandle.NW :
                    rSE.Contains(e.mousePosition) ? ResizeHandle.SE :
                    rSW.Contains(e.mousePosition) ? ResizeHandle.SW :
                    rN.Contains(e.mousePosition) ? ResizeHandle.N :
                    rS.Contains(e.mousePosition) ? ResizeHandle.S :
                    rE.Contains(e.mousePosition) ? ResizeHandle.E :
                    rW.Contains(e.mousePosition) ? ResizeHandle.W :
                    ResizeHandle.None;

                if (picked != ResizeHandle.None)
                {
                    var frame = model.GetCurrentFrame();
                    if (frame == null || index < 0 || index >= frame.Boxes.Count) return;

                    _resizing = true;
                    _activeHandle = picked;
                    _resizeStartMouse = e.mousePosition;
                    _resizeStartBox = frame.Boxes[index];

                    e.Use();
                    clickConsumed = true;
                    return;
                }
            }

            // Mouse drag: apply resize in local space
            if (_resizing && _activeHandle != ResizeHandle.None && e.type == EventType.MouseDrag && e.button == 0)
            {
                Vector2 deltaPx = e.mousePosition - _resizeStartMouse;

                float worldPerPixel = (cam.orthographicSize * 2f) / Mathf.Max(1f, rect.height);
                Vector2 deltaWorld = new Vector2(deltaPx.x * worldPerPixel, -deltaPx.y * worldPerPixel);

                Vector3 localDelta3 = root.InverseTransformVector(new Vector3(deltaWorld.x, deltaWorld.y, 0f));
                Vector2 d = new Vector2(localDelta3.x, localDelta3.y);

                // Work with edges in local space
                float cx = _resizeStartBox.CenterLocal.x;
                float cy = _resizeStartBox.CenterLocal.y;
                float hw = _resizeStartBox.SizeLocal.x * 0.5f;
                float hh = _resizeStartBox.SizeLocal.y * 0.5f;

                float left = cx - hw;
                float right = cx + hw;
                float bottom = cy - hh;
                float top = cy + hh;

                // Apply edge motion based on handle
                switch (_activeHandle)
                {
                    case ResizeHandle.E: right += d.x; break;
                    case ResizeHandle.W: left += d.x; break;
                    case ResizeHandle.N: top += d.y; break;
                    case ResizeHandle.S: bottom += d.y; break;

                    case ResizeHandle.NE: right += d.x; top += d.y; break;
                    case ResizeHandle.NW: left += d.x; top += d.y; break;
                    case ResizeHandle.SE: right += d.x; bottom += d.y; break;
                    case ResizeHandle.SW: left += d.x; bottom += d.y; break;
                }

                // Optional: Shift preserves aspect ratio from start box.
                if (e.shift)
                {
                    float startAspect = SafeAspect(_resizeStartBox.SizeLocal);
                    ApplyAspectConstraint(ref left, ref right, ref bottom, ref top, startAspect, _activeHandle);
                }

                // Prevent inverted/degenerate sizes
                const float minSize = 0.001f;
                if (right < left + minSize) right = left + minSize;
                if (top < bottom + minSize) top = bottom + minSize;

                BoxData updated = _resizeStartBox;
                updated.CenterLocal = new Vector2((left + right) * 0.5f, (bottom + top) * 0.5f);
                updated.SizeLocal = new Vector2(right - left, top - bottom);

                model.SetBox(index, updated);

                e.Use();
            }
        }

        private void HandleBackgroundDeselection(
            Rect rect,
            MoveBuilderModel model,
            ref bool clickConsumed)
        {
            Event e = Event.current;
            if (e == null) return;

            if (e.type == EventType.MouseDown &&
                e.button == 0 &&
                rect.Contains(e.mousePosition) &&
                !clickConsumed)
            {
                if (model.SelectedBoxIndex != -1)
                {
                    model.SelectBox(-1);
                }
                clickConsumed = true;
                e.Use();
            }
        }


        private static Rect HandleRect(float x, float y, float size)
        {
            return new Rect(x - size * 0.5f, y - size * 0.5f, size, size);
        }

        private static void DrawHandle(Rect r)
        {
            // Small solid square. Keep simple; no special colors beyond default.
            EditorGUI.DrawRect(r, Color.white);
        }

        private static float SafeAspect(Vector2 size)
        {
            float w = Mathf.Max(0.0001f, size.x);
            float h = Mathf.Max(0.0001f, size.y);
            return w / h;
        }

        private static void ApplyAspectConstraint(
            ref float left, ref float right, ref float bottom, ref float top,
            float aspect, ResizeHandle handle)
        {
            // Maintain width/height = aspect by adjusting the "secondary" axis.
            // For corner handles, we keep the dragged corner and adjust the opposite axis extent.
            // For edge handles, we adjust the orthogonal edges symmetrically around center.
            float width = right - left;
            float height = top - bottom;

            if (width <= 0f || height <= 0f) return;

            float targetHeight = width / aspect;
            float targetWidth = height * aspect;

            bool adjustHeight = Mathf.Abs(targetHeight - height) < Mathf.Abs(targetWidth - width);

            if (handle == ResizeHandle.N || handle == ResizeHandle.S || handle == ResizeHandle.E || handle == ResizeHandle.W)
            {
                // Edge: adjust orthogonal dimension around center
                float cy = (top + bottom) * 0.5f;
                float cx = (left + right) * 0.5f;

                if (handle == ResizeHandle.E || handle == ResizeHandle.W)
                {
                    // width is primary, adjust height
                    float hh = (width / aspect) * 0.5f;
                    top = cy + hh;
                    bottom = cy - hh;
                }
                else
                {
                    // height is primary, adjust width
                    float hw = (height * aspect) * 0.5f;
                    right = cx + hw;
                    left = cx - hw;
                }
            }
            else
            {
                // Corner: keep the dragged corner fixed, adjust the other axis
                if (adjustHeight)
                {
                    float newH = Mathf.Max(0.0001f, targetHeight);
                    switch (handle)
                    {
                        case ResizeHandle.NE: bottom = top - newH; break;
                        case ResizeHandle.NW: bottom = top - newH; break;
                        case ResizeHandle.SE: top = bottom + newH; break;
                        case ResizeHandle.SW: top = bottom + newH; break;
                    }
                }
                else
                {
                    float newW = Mathf.Max(0.0001f, targetWidth);
                    switch (handle)
                    {
                        case ResizeHandle.NE: left = right - newW; break;
                        case ResizeHandle.NW: right = left + newW; break;
                        case ResizeHandle.SE: left = right - newW; break;
                        case ResizeHandle.SW: right = left + newW; break;
                    }
                }
            }
        }

        private static Rect LocalBoxToGuiRect(Rect rect, Camera cam, Transform root, BoxData box)
        {
            float hw = box.SizeLocal.x * 0.5f;
            float hh = box.SizeLocal.y * 0.5f;

            Vector3 w0 = root.TransformPoint(new Vector3(box.CenterLocal.x - hw, box.CenterLocal.y - hh, 0));
            Vector3 w1 = root.TransformPoint(new Vector3(box.CenterLocal.x + hw, box.CenterLocal.y + hh, 0));

            Vector2 g0 = WorldToGui(rect, cam, w0);
            Vector2 g1 = WorldToGui(rect, cam, w1);

            return Rect.MinMaxRect(
                Mathf.Min(g0.x, g1.x),
                Mathf.Min(g0.y, g1.y),
                Mathf.Max(g0.x, g1.x),
                Mathf.Max(g0.y, g1.y)
            );
        }

        private static Vector2 WorldToGui(Rect rect, Camera cam, Vector3 world)
        {
            // PreviewRenderUtility renders into a texture sized: rect * pixelsPerPoint.
            // WorldToScreenPoint returns pixel coordinates in that render target.
            Vector3 sp = cam.WorldToScreenPoint(world);

            float ppp = EditorGUIUtility.pixelsPerPoint;
            float texW = Mathf.Max(1f, rect.width * ppp);
            float texH = Mathf.Max(1f, rect.height * ppp);

            float x = rect.xMin + (sp.x / texW) * rect.width;
            float y = rect.yMin + (1f - (sp.y / texH)) * rect.height;

            return new Vector2(x, y);
        }

        private static void DrawRectOutline(Rect r, float t, Color color)
        {
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, r.width, t), color);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMax - t, r.width, t), color);
            EditorGUI.DrawRect(new Rect(r.xMin, r.yMin, t, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - t, r.yMin, t, r.height), color);
        }

        private static void DrawCentered(Rect r, string text)
        {
            var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            GUI.Label(r, text, style);
        }
    }
}
