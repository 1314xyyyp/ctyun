using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CtYun
{
    /// <summary>
    /// 验证码识别：优先使用内置的 ddddocr(common_old) 模型离线推理；
    /// 本地模型不可用或推理失败时回退到远程接口。
    /// 远程请求使用独立 HttpClient，不携带任何 ctg-* 官方请求头，避免向第三方泄露设备码。
    /// </summary>
    internal static class OcrService
    {
        private static readonly Lazy<LocalDdddOcr> Local =
            new(() => LocalDdddOcr.TryLoad(), LazyThreadSafetyMode.PublicationOnly);

        // 独立的远程识别客户端：与官方 API 客户端完全隔离
        private static readonly HttpClient RemoteClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        public static bool LocalAvailable => Local.Value != null;

        public static async Task<string> RecognizeAsync(byte[] image, string remoteUrl)
        {
            var local = Local.Value;
            if (local != null)
            {
                try
                {
                    var code = local.Recognize(image);
                    Utility.WriteLine(ConsoleColor.Green, $"识别结果（本地模型）：{code}");
                    return code;
                }
                catch (Exception ex)
                {
                    Utility.WriteLine(ConsoleColor.DarkYellow, $"本地识别失败，回退远程：{ex.Message}");
                }
            }

            return await RecognizeRemoteAsync(image, remoteUrl);
        }

        private static async Task<string> RecognizeRemoteAsync(byte[] image, string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                Utility.WriteLine(ConsoleColor.Red, "本地模型不可用且未配置远程识别接口（ocrUrl）。");
                return "";
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, remoteUrl);
                request.Content = new MultipartFormDataContent
                {
                    { new StringContent(Convert.ToBase64String(image)), "image" }
                };
                using var response = await RemoteClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadAsStringAsync();
                Utility.WriteLine(ConsoleColor.Green, $"识别结果（远程 {new Uri(remoteUrl).Host}）：{result}");
                using var doc = JsonDocument.Parse(result);
                return doc.RootElement.GetProperty("data").GetString();
            }
            catch (Exception ex)
            {
                Utility.WriteLine(ConsoleColor.Red, "验证码识别错误：" + ex.Message);
                return "";
            }
        }
    }

    /// <summary>
    /// ddddocr common_old 模型的本地推理实现，
    /// 预处理与解码逻辑对齐 ddddocr 1.6.1：高 64 等比缩放(Lanczos) → 灰度 → /255 → CTC 解码。
    /// </summary>
    internal sealed class LocalDdddOcr : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string[] _charset;

        public static LocalDdddOcr TryLoad()
        {
            var path = FindModel();
            if (path == null)
            {
                Utility.WriteLine(ConsoleColor.DarkYellow, "未找到本地 OCR 模型（models/ddddocr.onnx），验证码将使用远程识别。");
                return null;
            }

            try
            {
                var ocr = new LocalDdddOcr(path);
                Utility.WriteLine(ConsoleColor.Green, $"本地 OCR 模型已加载：{path}");
                return ocr;
            }
            catch (Exception ex)
            {
                Utility.WriteLine(ConsoleColor.DarkYellow, $"本地 OCR 模型加载失败（回退远程识别）：{ex.Message}");
                return null;
            }
        }

        private static string FindModel()
        {
            var dataDir = Environment.GetEnvironmentVariable("CTYUN_DATA_DIR");
            var candidates = new[]
            {
                string.IsNullOrWhiteSpace(dataDir) ? null : Path.Combine(dataDir, "models", "ddddocr.onnx"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "ddddocr.onnx"),
                Path.Combine(AppContext.BaseDirectory, "models", "ddddocr.onnx"), // 兼容旧目录布局
            };
            return candidates.FirstOrDefault(p => p != null && File.Exists(p));
        }

        private LocalDdddOcr(string modelPath)
        {
            _session = new InferenceSession(modelPath);
            _inputName = _session.InputMetadata.Keys.First();

            // 字符表：Data 为 charset[1..]，运行时头部补 CTC blank
            var data = DdddCharset.Data;
            _charset = new string[data.Length + 1];
            for (int i = 0; i < data.Length; i++)
            {
                _charset[i + 1] = data[i].ToString();
            }

            var outMeta = _session.OutputMetadata.Values.First();
            var classes = outMeta.Dimensions.Length >= 2 ? outMeta.Dimensions[^1] : 0;
            if (classes > 0 && classes != _charset.Length)
            {
                throw new InvalidOperationException($"模型类别数 {classes} 与字符表长度 {_charset.Length} 不一致，模型与字符表不匹配");
            }
        }

        public string Recognize(byte[] imageBytes)
        {
            using var image = Image.Load<Rgba32>(imageBytes);

            int targetWidth = (int)(image.Width * (64.0 / image.Height));
            if (image.Height != 64)
            {
                image.Mutate(x => x.Resize(targetWidth, 64, KnownResamplers.Lanczos3));
            }
            int width = image.Width;

            var input = new float[64 * width];
            for (int y = 0; y < 64; y++)
            {
                var row = image.DangerousGetPixelRowMemory(y).Span;
                for (int x = 0; x < width; x++)
                {
                    var px = row[x];
                    // 与 PIL convert('L') 一致：L = (299R + 587G + 114B) / 1000（截断）
                    int gray = (px.R * 299 + px.G * 587 + px.B * 114) / 1000;
                    input[y * width + x] = gray / 255f;
                }
            }

            var tensor = new DenseTensor<float>(input, new[] { 1, 1, 64, width });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var results = _session.Run(inputs);
            return Decode(results.First().AsTensor<float>());
        }

        private string Decode(Tensor<float> output)
        {
            var dims = output.Dimensions;
            int layout;
            int timesteps;
            int classes;
            if (dims.Length == 3 && dims[1] == 1)      // (T, 1, C)
            {
                layout = 1; timesteps = dims[0]; classes = dims[2];
            }
            else if (dims.Length == 3)                  // (1, T, C)
            {
                layout = 0; timesteps = dims[1]; classes = dims[2];
            }
            else if (dims.Length == 2)                  // (T, C)
            {
                layout = 2; timesteps = dims[0]; classes = dims[1];
            }
            else
            {
                throw new InvalidOperationException($"不支持的模型输出维度：{dims.Length}D");
            }

            float Get(int t, int c) => layout switch
            {
                0 => output[0, t, c],
                1 => output[t, 0, c],
                _ => output[t, c],
            };

            var sb = new StringBuilder();
            int last = -1;
            for (int t = 0; t < timesteps; t++)
            {
                int best = 0;
                float bestValue = float.NegativeInfinity;
                for (int c = 0; c < classes && c < _charset.Length; c++)
                {
                    var v = Get(t, c);
                    if (v > bestValue)
                    {
                        bestValue = v;
                        best = c;
                    }
                }

                // CTC：跳过连续重复与 blank(0)
                if (best != last && best != 0)
                {
                    sb.Append(_charset[best]);
                }
                last = best;
            }
            return sb.ToString();
        }

        public void Dispose() => _session.Dispose();
    }
}
