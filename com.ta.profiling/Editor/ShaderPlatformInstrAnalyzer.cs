using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace TA.Profiling.Editor
{
    public enum ShaderCompileTargetPreset
    {
        WindowsD3D = 0,
        AndroidVulkan = 1,
        AndroidGLES3 = 2,
        IosMetal = 3
    }

    [Serializable]
    public sealed class ShaderStageCompileResult
    {
        public string platformLabel;
        public string stageLabel;
        public bool success;
        public bool stagesCombined;
        public int instructionCount = -1;
        public string instructionSource = "none";
        public int bytecodeBytes;
        public int constantBuffers;
        public int textureBindings;
        public int attributes;
        public string message;
    }

    [Serializable]
    public sealed class ShaderPassCompileRow
    {
        public string shaderName;
        public string materialName;
        public string passName;
        public int passIndex;
        public int passCount;
        public int keywordCount;
        public string keywordsJoined;
        public bool srpBatcherOk;
        public List<ShaderStageCompileResult> stages = new List<ShaderStageCompileResult>();

        public int MaxInstructions
        {
            get
            {
                int m = -1;
                foreach (var s in stages)
                {
                    if (s.instructionCount > m) m = s.instructionCount;
                }

                return m;
            }
        }

        public int MaxBytecode
        {
            get
            {
                int m = 0;
                foreach (var s in stages)
                {
                    if (s.bytecodeBytes > m) m = s.bytecodeBytes;
                }

                return m;
            }
        }
    }

    [Serializable]
    public sealed class ShaderPlatformInstrReport
    {
        public string generatedAt;
        public string sceneName;
        public int shaderCount;
        public int rowCount;
        public List<string> platforms = new List<string>();
        public List<ShaderPassCompileRow> rows = new List<ShaderPassCompileRow>();
        public string summary;
    }

    /// <summary>
    /// TA：按平台编译 Shader 变体，尽量提取指令数（D3D 优先 fxc /dumpbin），并给出绑定数/字节码等代理指标。
    /// </summary>
    public static class ShaderPlatformInstrAnalyzer
    {
        private static readonly Regex InstrRegex = new Regex(
            @"(?:approximately\s+)?(\d+)\s+instruction\s+slots?\s+used|" +
            @"instruction\s+slots?\s*:\s*(\d+)|" +
            @"ALU\s*(?:instructions?)?\s*[:=]\s*(\d+)|" +
            @"Approx(?:imate)?(?:ly)?\s+(?:math\s+)?instruction\s+count\s*[:=]\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static (ShaderCompilerPlatform platform, BuildTarget target, string label, bool combinedStages)
            Resolve(ShaderCompileTargetPreset preset)
        {
            switch (preset)
            {
                case ShaderCompileTargetPreset.AndroidVulkan:
                    return (ShaderCompilerPlatform.Vulkan, BuildTarget.Android, "Android·Vulkan", true);
                case ShaderCompileTargetPreset.AndroidGLES3:
                    return (ShaderCompilerPlatform.GLES3x, BuildTarget.Android, "Android·GLES3", true);
                case ShaderCompileTargetPreset.IosMetal:
                    return (ShaderCompilerPlatform.Metal, BuildTarget.iOS, "iOS·Metal", false);
                default:
                    return (ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, "Win·D3D", false);
            }
        }

        public static ShaderPlatformInstrReport ScanScene(
            IList<ShaderCompileTargetPreset> presets,
            bool includeInactive = true,
            bool preferSelection = true,
            bool oneMaterialPerShader = true)
        {
            var report = new ShaderPlatformInstrReport
            {
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                sceneName = SceneManager.GetActiveScene().name
            };

            if (presets == null || presets.Count == 0)
            {
                report.summary = "未选择平台。";
                return report;
            }

            foreach (var p in presets)
                report.platforms.Add(Resolve(p).label);

            var materials = CollectMaterials(includeInactive, preferSelection, out var temps);
            var seenShaderKw = new HashSet<string>();
            var seenShader = new HashSet<string>();

            try
            {
                EditorUtility.DisplayProgressBar("TA Shader 编译", "准备…", 0f);
                int done = 0;
                int total = Mathf.Max(1, materials.Count);
                foreach (var mat in materials)
                {
                    done++;
                    if (mat == null || mat.shader == null) continue;

                    // 默认：每个 Shader 只取一份代表材质，避免场景里几十个 Lit 变体刷屏
                    if (oneMaterialPerShader)
                    {
                        if (!seenShader.Add(mat.shader.name)) continue;
                    }
                    else
                    {
                        string key = mat.shader.name + "||" + GetKeywordKey(mat);
                        if (!seenShaderKw.Add(key)) continue;
                    }

                    EditorUtility.DisplayProgressBar(
                        "TA Shader 编译",
                        mat.shader.name,
                        done / (float)total);

                    AnalyzeMaterial(mat, presets, report.rows);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                foreach (var t in temps)
                {
                    if (t != null) UnityEngine.Object.DestroyImmediate(t);
                }
            }

            report.rows.Sort((a, b) =>
            {
                int byName = string.CompareOrdinal(a.shaderName, b.shaderName);
                if (byName != 0) return byName;
                int byPass = a.passIndex.CompareTo(b.passIndex);
                if (byPass != 0) return byPass;
                return string.CompareOrdinal(a.keywordsJoined, b.keywordsJoined);
            });

            report.rowCount = report.rows.Count;
            report.shaderCount = report.rows.Select(r => r.shaderName).Distinct().Count();
            int withInstr = report.rows.Count(r => r.MaxInstructions >= 0);
            report.summary =
                $"着色器 {report.shaderCount} · Pass 行 {report.rowCount} · 已解析指令 {withInstr} · " +
                (oneMaterialPerShader ? "每Shader一份 · " : "含Keywords变体 · ") +
                $"平台 {string.Join("/", report.platforms)}";
            return report;
        }

        public static string ToMarkdown(ShaderPlatformInstrReport report)
        {
            if (report == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("# TA Shader 平台编译报告");
            sb.AppendLine();
            sb.AppendLine($"- 时间：{report.generatedAt}");
            sb.AppendLine($"- 场景：{report.sceneName}");
            sb.AppendLine($"- 摘要：{report.summary}");
            sb.AppendLine();
            sb.AppendLine("| Shader | Pass | Platform | Stage | Instr | Bytes | Tex | CB | Attr | Note |");
            sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---|");
            foreach (var row in report.rows)
            {
                foreach (var st in row.stages)
                {
                    string instr = st.instructionCount >= 0 ? st.instructionCount.ToString() : "—";
                    sb.Append("| ").Append(EscapeMd(row.shaderName))
                        .Append(" | ").Append(EscapeMd(row.passName))
                        .Append(" | ").Append(st.platformLabel)
                        .Append(" | ").Append(st.stageLabel)
                        .Append(" | ").Append(instr)
                        .Append(" | ").Append(st.bytecodeBytes)
                        .Append(" | ").Append(st.textureBindings)
                        .Append(" | ").Append(st.constantBuffers)
                        .Append(" | ").Append(st.attributes)
                        .Append(" | ").Append(EscapeMd(st.message ?? ""))
                        .AppendLine(" |");
                }
            }

            sb.AppendLine();
            sb.AppendLine("> Instr 优先来自 D3D fxc 反汇编；其它平台若无「—」请看 Bytes/Tex/CB。");
            return sb.ToString();
        }

        /// <summary>
        /// OpenCompiledShader 为 internal，反射调用（等同 Inspector「Compile and show code」）。
        /// </summary>
        public static bool TryOpenCompiledShader(Shader shader, out string error)
        {
            error = null;
            if (shader == null)
            {
                error = "Shader 为空";
                return false;
            }

            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                // Unity 2021+ : (Shader, int mode, int mask, bool includeAll, bool preprocessOnly, bool stripLine)
                var m6 = typeof(ShaderUtil).GetMethod(
                    "OpenCompiledShader", flags, null,
                    new[] { typeof(Shader), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool) },
                    null);
                if (m6 != null)
                {
                    m6.Invoke(null, new object[] { shader, 1, ~0, false, false, false });
                    return true;
                }

                // 旧签名: (Shader, int, int, bool)
                var m4 = typeof(ShaderUtil).GetMethod(
                    "OpenCompiledShader", flags, null,
                    new[] { typeof(Shader), typeof(int), typeof(int), typeof(bool) },
                    null);
                if (m4 != null)
                {
                    m4.Invoke(null, new object[] { shader, 1, ~0, false });
                    return true;
                }

                error = "当前 Unity 无 OpenCompiledShader，请在 Inspector 手动 Compile and show code";
                return false;
            }
            catch (Exception ex)
            {
                error = "OpenCompiledShader 失败: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        private static void AnalyzeMaterial(
            Material mat,
            IList<ShaderCompileTargetPreset> presets,
            List<ShaderPassCompileRow> rows)
        {
            var shader = mat.shader;
            string[] keywords = GetMaterialKeywords(mat);
            bool srpOk = IsSrpBatcherFriendly(mat);

            ShaderData data;
            try
            {
                data = ShaderUtil.GetShaderData(shader);
            }
            catch (Exception ex)
            {
                rows.Add(new ShaderPassCompileRow
                {
                    shaderName = shader.name,
                    materialName = mat.name,
                    passName = "(GetShaderData 失败)",
                    passCount = mat.passCount,
                    keywordCount = keywords.Length,
                    keywordsJoined = string.Join(", ", keywords),
                    srpBatcherOk = srpOk,
                    stages =
                    {
                        new ShaderStageCompileResult
                        {
                            platformLabel = "-",
                            stageLabel = "-",
                            success = false,
                            message = ex.Message
                        }
                    }
                });
                return;
            }

            int subCount = data.SubshaderCount;
            if (subCount <= 0) return;

            var sub = data.GetSubshader(0);
            int passCount = sub.PassCount;
            for (int pi = 0; pi < passCount; pi++)
            {
                var pass = sub.GetPass(pi);
                string passName = string.IsNullOrEmpty(pass.Name) ? $"Pass {pi}" : pass.Name;
                var row = new ShaderPassCompileRow
                {
                    shaderName = shader.name,
                    materialName = mat.name,
                    passName = passName,
                    passIndex = pi,
                    passCount = passCount,
                    keywordCount = keywords.Length,
                    keywordsJoined = string.Join(", ", keywords),
                    srpBatcherOk = srpOk
                };

                foreach (var preset in presets)
                {
                    var (platform, target, label, combined) = Resolve(preset);
                    BuiltinShaderDefine[] platformKw = null;
                    try
                    {
                        platformKw = ShaderUtil.GetShaderPlatformKeywordsForBuildTarget(platform, target);
                    }
                    catch
                    {
                        platformKw = null;
                    }

                    if (combined)
                    {
                        row.stages.Add(CompileOne(
                            pass, ShaderType.Vertex, keywords, platform, target, platformKw,
                            label, "All(VS入口)", true));
                    }
                    else
                    {
                        if (pass.HasShaderStage(ShaderType.Vertex))
                        {
                            row.stages.Add(CompileOne(
                                pass, ShaderType.Vertex, keywords, platform, target, platformKw,
                                label, "VS", false));
                        }

                        if (pass.HasShaderStage(ShaderType.Fragment))
                        {
                            row.stages.Add(CompileOne(
                                pass, ShaderType.Fragment, keywords, platform, target, platformKw,
                                label, "PS", false));
                        }
                    }
                }

                if (row.stages.Count > 0)
                    rows.Add(row);
            }
        }

        private static ShaderStageCompileResult CompileOne(
            ShaderData.Pass pass,
            ShaderType stage,
            string[] keywords,
            ShaderCompilerPlatform platform,
            BuildTarget target,
            BuiltinShaderDefine[] platformKw,
            string platformLabel,
            string stageLabel,
            bool combined)
        {
            var result = new ShaderStageCompileResult
            {
                platformLabel = platformLabel,
                stageLabel = stageLabel,
                stagesCombined = combined
            };

            try
            {
                ShaderData.VariantCompileInfo info = platformKw != null
                    ? pass.CompileVariant(stage, keywords, platform, target, platformKw)
                    : pass.CompileVariant(stage, keywords, platform, target);

                result.success = info.Success;
                result.bytecodeBytes = info.ShaderData != null ? info.ShaderData.Length : 0;
                result.constantBuffers = info.ConstantBuffers != null ? info.ConstantBuffers.Length : 0;
                result.textureBindings = info.TextureBindings != null ? info.TextureBindings.Length : 0;
                result.attributes = info.Attributes != null ? info.Attributes.Length : 0;

                if (!info.Success)
                {
                    result.message = SummarizeMessages(info.Messages);
                    return result;
                }

                if (info.ShaderData == null || info.ShaderData.Length == 0)
                {
                    result.message = "空字节码（该 Pass/Stage 可能不适用）";
                    return result;
                }

                if (TryParseInstructionsFromBytes(info.ShaderData, out int fromText, out string srcText))
                {
                    result.instructionCount = fromText;
                    result.instructionSource = srcText;
                }
                else if (platform == ShaderCompilerPlatform.D3D
                         && TryFxcDumpInstructions(info.ShaderData, out int fromFxc, out string srcFxc))
                {
                    result.instructionCount = fromFxc;
                    result.instructionSource = srcFxc;
                }
                else
                {
                    // 外部工具源码再试一次（部分后端会给出可读统计）
                    try
                    {
                        var ext = platformKw != null
                            ? pass.CompileVariant(stage, keywords, platform, target, platformKw, true)
                            : pass.CompileVariant(stage, keywords, platform, target, true);
                        if (ext.Success && ext.ShaderData != null
                            && TryParseInstructionsFromBytes(ext.ShaderData, out int fromExt, out string srcExt))
                        {
                            result.instructionCount = fromExt;
                            result.instructionSource = srcExt;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (result.instructionCount < 0)
                    result.message = $"无指令字段 · 源={result.instructionSource}";
                else
                    result.message = $"源={result.instructionSource}";
            }
            catch (Exception ex)
            {
                result.success = false;
                result.message = ex.Message;
            }

            return result;
        }

        private static bool TryParseInstructionsFromBytes(byte[] data, out int count, out string source)
        {
            count = -1;
            source = "none";
            if (data == null || data.Length == 0) return false;

            // 仅当看起来像文本时解析
            int sample = Mathf.Min(data.Length, 64);
            int printable = 0;
            for (int i = 0; i < sample; i++)
            {
                byte b = data[i];
                if (b == 9 || b == 10 || b == 13 || (b >= 32 && b < 127)) printable++;
            }

            if (printable < sample * 0.85f) return false;

            string text = Encoding.UTF8.GetString(data);
            var m = InstrRegex.Match(text);
            if (!m.Success) return false;
            for (int g = 1; g < m.Groups.Count; g++)
            {
                if (!m.Groups[g].Success) continue;
                if (int.TryParse(m.Groups[g].Value, out count) && count >= 0)
                {
                    source = "text";
                    return true;
                }
            }

            return false;
        }

        private static bool TryFxcDumpInstructions(byte[] dxbc, out int count, out string source)
        {
            count = -1;
            source = "none";
            if (dxbc == null || dxbc.Length < 4) return false;
            // DXBC magic
            if (!(dxbc[0] == (byte)'D' && dxbc[1] == (byte)'X' && dxbc[2] == (byte)'B' && dxbc[3] == (byte)'C'))
                return false;

            string fxc = FindFxcPath();
            if (string.IsNullOrEmpty(fxc)) return false;

            string tmpDir = Path.Combine(Path.GetTempPath(), "TAProfilingShader");
            Directory.CreateDirectory(tmpDir);
            string binPath = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".cso");
            try
            {
                File.WriteAllBytes(binPath, dxbc);
                var psi = new ProcessStartInfo
                {
                    FileName = fxc,
                    Arguments = $"/dumpbin /nologo \"{binPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(15000);
                    string all = stdout + "\n" + stderr;
                    var m = InstrRegex.Match(all);
                    if (!m.Success) return false;
                    for (int g = 1; g < m.Groups.Count; g++)
                    {
                        if (!m.Groups[g].Success) continue;
                        if (int.TryParse(m.Groups[g].Value, out count) && count >= 0)
                        {
                            source = "fxc";
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TA.Profiling] fxc dump 失败: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(binPath)) File.Delete(binPath); } catch { /* ignore */ }
            }

            return false;
        }

        private static string FindFxcPath()
        {
            string kits = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Windows Kits", "10", "bin");
            if (!Directory.Exists(kits)) return null;

            try
            {
                var versions = Directory.GetDirectories(kits)
                    .Select(Path.GetFileName)
                    .Where(v => v != null && char.IsDigit(v[0]))
                    .OrderByDescending(v => v)
                    .ToList();
                foreach (var ver in versions)
                {
                    string cand = Path.Combine(kits, ver, "x64", "fxc.exe");
                    if (File.Exists(cand)) return cand;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static List<Material> CollectMaterials(
            bool includeInactive,
            bool preferSelection,
            out List<Material> temps)
        {
            var list = new List<Material>();
            temps = new List<Material>();
            var set = new HashSet<Material>();

            void AddMat(Material m)
            {
                if (m == null || m.shader == null) return;
                if (m.shader.name.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase)) return;
                if (set.Add(m)) list.Add(m);
            }

            if (preferSelection)
            {
                foreach (var obj in Selection.objects)
                {
                    if (obj is Material mat) AddMat(mat);
                    else if (obj is Shader sh)
                    {
                        var temp = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                        temps.Add(temp);
                        AddMat(temp);
                    }
                    else if (obj is GameObject go)
                    {
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            if (r.sharedMaterials == null) continue;
                            foreach (var m in r.sharedMaterials) AddMat(m);
                        }
                    }
                }
            }

            var renderers = includeInactive
                ? Resources.FindObjectsOfTypeAll<Renderer>()
                : UnityEngine.Object.FindObjectsOfType<Renderer>();

            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!r.gameObject.scene.IsValid()) continue;
                if (r.sharedMaterials == null) continue;
                foreach (var m in r.sharedMaterials) AddMat(m);
            }

            return list;
        }

        private static string[] GetMaterialKeywords(Material mat)
        {
            if (mat == null) return Array.Empty<string>();
            try
            {
                var enabled = mat.enabledKeywords;
                if (enabled == null || enabled.Length == 0) return Array.Empty<string>();
                var names = new string[enabled.Length];
                for (int i = 0; i < enabled.Length; i++)
                    names[i] = enabled[i].name;
                return names;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string GetKeywordKey(Material mat)
        {
            var kws = GetMaterialKeywords(mat);
            if (kws.Length == 0) return "";
            Array.Sort(kws, StringComparer.Ordinal);
            return string.Join(";", kws);
        }

        private static string SummarizeMessages(ShaderMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return "compile failed";
            var sb = new StringBuilder();
            int n = Mathf.Min(3, messages.Length);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(messages[i].message);
            }

            return sb.ToString();
        }

        private static bool IsSrpBatcherFriendly(Material mat)
        {
            if (mat == null || mat.shader == null) return false;
            try
            {
                var method = typeof(ShaderUtil).GetMethod(
                    "GetSRPBatcherCompatibilityCode",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                {
                    object result = method.Invoke(null, new object[] { mat.shader, 0 });
                    if (result is int code) return code == 0;
                }
            }
            catch { /* fallthrough */ }

            string n = mat.shader.name ?? "";
            if (n.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase)) return false;
            return n.IndexOf("Universal Render Pipeline", StringComparison.Ordinal) >= 0
                   || n.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0
                   || mat.enableInstancing;
        }

        private static string EscapeMd(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "/").Replace("\n", " ");
        }
    }
}
