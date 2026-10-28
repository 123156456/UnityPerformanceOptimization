using System;
using UnityEditor;
using UnityEngine;

namespace TA.Profiling.Editor
{
    /// <summary>
    /// 实时跟踪 DrawCall / SetPassCall / Batches / Tris（UnityStats）。
    /// </summary>
    public sealed class RenderStatsTracker
    {
        public const int HistoryLength = 90;

        private readonly float[] _drawCalls = new float[HistoryLength];
        private readonly float[] _setPass = new float[HistoryLength];
        private readonly float[] _batches = new float[HistoryLength];
        private int _index;
        private int _filled;

        public int DrawCalls { get; private set; }
        public int SetPassCalls { get; private set; }
        public int Batches { get; private set; }
        public int Triangles { get; private set; }
        public int Vertices { get; private set; }
        public int DrawCallsPeak { get; private set; }
        public int SetPassPeak { get; private set; }
        public float[] DrawCallHistory => _drawCalls;
        public float[] SetPassHistory => _setPass;
        public float[] BatchHistory => _batches;
        public int HistoryIndex => _index;
        public int HistoryFilled => _filled;

        public void Tick()
        {
            try
            {
                DrawCalls = UnityStats.drawCalls;
                SetPassCalls = UnityStats.setPassCalls;
                Batches = UnityStats.batches;
                Triangles = UnityStats.triangles;
                Vertices = UnityStats.vertices;
            }
            catch
            {
                return;
            }

            _drawCalls[_index % HistoryLength] = DrawCalls;
            _setPass[_index % HistoryLength] = SetPassCalls;
            _batches[_index % HistoryLength] = Batches;
            _index++;
            if (_filled < HistoryLength) _filled++;

            if (DrawCalls > DrawCallsPeak) DrawCallsPeak = DrawCalls;
            if (SetPassCalls > SetPassPeak) SetPassPeak = SetPassCalls;
        }

        public void ResetPeaks()
        {
            DrawCallsPeak = DrawCalls;
            SetPassPeak = SetPassCalls;
        }
    }
}
