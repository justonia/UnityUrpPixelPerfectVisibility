// MIT License
//
// Copyright (c) 2023 Justin Larrabee <justonia@gmail.com>
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Collections;

namespace PixelPerfectVisibility.Example
{
    public class PixelPerfectSelectionMouseHover : MonoBehaviour
    {
        [Range(0f, 1f)]
        public float PercentOfScreenIgnore = 0.005f;

        private void LateUpdate()
        {
            var pixelCam = PixelPerfectVisibilityCamera.main;
            if (pixelCam == null) {
                return;
            }

            var pos = Input.mousePosition;

            var highlighted = pixelCam.GetRendererAtScreenPosition(pos.x, pos.y);

            foreach (var renderer in PixelPerfectVisibilityCamera.Renderers) {
                var ex = renderer.GetComponent<PixelPerfectSelectionExampleObject>();
                if (ex != null) {
                    ex.IsHighlighted = renderer == highlighted &&
                        pixelCam.TryGetVisiblity(renderer, out _, out var percentOfScreen) &&
                        percentOfScreen >= PercentOfScreenIgnore;
                }
            }
        }
    }
}
