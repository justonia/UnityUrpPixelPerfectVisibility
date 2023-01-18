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
    public class PixelPerfectSelectionExampleObject : MonoBehaviour
    {
        private PixelPerfectVisibilityRenderer visibilityRenderer;

        private int _lerpParam = Shader.PropertyToID("_ColorLerp");
        private int _highlightParam = Shader.PropertyToID("_HighlightLerp");

        public bool IsHighlighted { get; set; }

        private void Awake()
        {
            visibilityRenderer = GetComponent<PixelPerfectVisibilityRenderer>();
        }

        private void LateUpdate()
        {
            var cam = PixelPerfectVisibilityCamera.main;
            if (cam != null) {
                visibilityRenderer.TargetRenderer.material.SetFloat(_highlightParam, IsHighlighted ? 1f : 0f);
                visibilityRenderer.TargetRenderer.material.SetFloat(_lerpParam, cam.IsVisible(visibilityRenderer) ? 1f : 0f);
            }
        }
    }
}

