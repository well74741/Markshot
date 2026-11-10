using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Markshot.Services;

/// <summary>
/// Recognition models that are not part of Markshot (large, needed by few): downloaded by hand in the settings, as the
/// offline translation packs are. The everyday text recognition (PP-OCRv5, built in) is unchanged.
/// </summary>
public static class OcrPacks
{
    public sealed record Pack(string Id, string Repo, string Label, string Description, long ApproxBytes, (string Remote, string Local)[] Files);

    public static readonly Pack[] All =
    {
        new("formula", "breezedeus/pix2text-mfr-1.5", "公式识别",
            "把公式图片识别成 LaTeX（分数、上下标、根号、积分、矩阵…）。Pix2Text MFR 1.5，MIT 许可。", 120_000_000,
            new[] { ("tokenizer.json", "tokenizer.json"), ("encoder_model.onnx", "encoder.onnx"), ("decoder_model.onnx", "decoder.onnx") }),
    };

    /// <summary>Tests: packs somewhere else.</summary>
    public static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Markshot", "ocr");

    public static string Folder(Pack pack) => Path.Combine(Root, pack.Id);

    public static Pack? Find(string id) => All.FirstOrDefault(pack => pack.Id == id);

    public static bool IsInstalled(Pack pack) => pack.Files.All(file => File.Exists(Path.Combine(Folder(pack), file.Local)));

    public static Pack Formula => Find("formula")!;

    public static bool FormulaInstalled => IsInstalled(Formula);

    public static long SizeOnDisk(Pack pack)
    {
        var folder = Folder(pack);
        return Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles().Where(file => file.Extension != ".part").Sum(file => file.Length) : 0;
    }

    public static async Task DownloadAsync(Pack pack, string mirrorId, IProgress<(long Done, long Total)>? progress, CancellationToken token)
    {
        await OfflinePacks.FetchAsync(pack.Repo, pack.Files, Folder(pack), pack.ApproxBytes, mirrorId, progress, token);
        FormulaRecognizer.Forget();
    }

    public static void Delete(Pack pack)
    {
        FormulaRecognizer.Forget();
        var folder = Folder(pack);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>
/// A formula picture → LaTeX (Pix2Text MFR: a vision encoder and a text decoder, greedy decoding; the tokens are
/// byte-level BPE, only decoded here).
/// </summary>
public sealed class FormulaRecognizer : IDisposable
{
    private const int Size = 384;
    private const int Bos = 1;
    private const int Eos = 2;
    private static readonly object Gate = new();
    private static FormulaRecognizer? _loaded;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly string[] _tokens;
    private readonly HashSet<int> _special = new();
    private readonly Dictionary<char, byte> _bytes = ByteDecoder();

    private FormulaRecognizer(string folder)
    {
        var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        _encoder = new InferenceSession(Path.Combine(folder, "encoder.onnx"), options);
        _decoder = new InferenceSession(Path.Combine(folder, "decoder.onnx"), options);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "tokenizer.json")));
        var vocab = json.RootElement.GetProperty("model").GetProperty("vocab");
        var count = vocab.EnumerateObject().Max(entry => entry.Value.GetInt32()) + 1;
        _tokens = new string[count];
        foreach (var entry in vocab.EnumerateObject())
        {
            _tokens[entry.Value.GetInt32()] = entry.Name;
        }

        if (json.RootElement.TryGetProperty("added_tokens", out var added))
        {
            foreach (var token in added.EnumerateArray())
            {
                var id = token.GetProperty("id").GetInt32();
                if (id < _tokens.Length)
                {
                    _tokens[id] = token.GetProperty("content").GetString() ?? "";
                }

                if (token.TryGetProperty("special", out var special) && special.GetBoolean())
                {
                    _special.Add(id);
                }
            }
        }
    }

    public static FormulaRecognizer Get()
    {
        lock (Gate)
        {
            return _loaded ??= new FormulaRecognizer(OcrPacks.Folder(OcrPacks.Formula));
        }
    }

    public static void Forget()
    {
        lock (Gate)
        {
            _loaded?.Dispose();
            _loaded = null;
        }
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }

    public static Task<string> RecognizeAsync(BitmapSource image, CancellationToken token = default)
    {
        if (!image.IsFrozen)
        {
            image = image.CanFreeze ? image.Clone() : new WriteableBitmap(image);
            image.Freeze();
        }

        return Task.Run(() => Get().Recognize(image, token), token);
    }

    /// <summary>The LaTeX of the formula in the picture.</summary>
    public string Recognize(BitmapSource image, CancellationToken token = default)
    {
        var pixels = Prepare(image);
        lock (Gate)
        {
            var encoderInput = _encoder.InputMetadata.Keys.First();
            using var encoded = _encoder.Run(new[] { NamedOnnxValue.CreateFromTensor(encoderInput, pixels) });
            var hidden = encoded.First().AsTensor<float>().ToDenseTensor();
            var idsName = _decoder.InputMetadata.Keys.First(name => name.Contains("input_ids", StringComparison.Ordinal));
            var hiddenName = _decoder.InputMetadata.Keys.First(name => name.Contains("encoder_hidden_states", StringComparison.Ordinal));
            var ids = new List<long> { Bos };
            for (var step = 0; step < 512; step++)
            {
                token.ThrowIfCancellationRequested();
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(idsName, new DenseTensor<long>(ids.ToArray(), new[] { 1, ids.Count })),
                    NamedOnnxValue.CreateFromTensor(hiddenName, hidden)
                };
                using var results = _decoder.Run(inputs);
                var logits = results.First(result => result.Name == "logits").AsTensor<float>();
                var vocab = logits.Dimensions[2];
                var last = ids.Count - 1;
                var best = 0;
                var bestScore = float.NegativeInfinity;
                for (var v = 0; v < vocab; v++)
                {
                    var score = logits[0, last, v];
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = v;
                    }
                }

                if (best == Eos)
                {
                    break;
                }

                ids.Add(best);
            }

            return Decode(ids.Skip(1));
        }
    }

    /// <summary>The picture as the model sees it: 384 × 384 RGB, scaled (bicubic) without keeping the shape, −1…1.</summary>
    private static DenseTensor<float> Prepare(BitmapSource image)
    {
        // On white first (a see-through picture would turn black), then scaled.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Size, Size));
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(image, new Rect(0, 0, Size, Size));
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var px = new byte[Size * Size * 4];
        bitmap.CopyPixels(px, Size * 4, 0);
        var tensor = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = (y * Size + x) * 4;
                tensor[0, 0, y, x] = px[i + 2] / 127.5f - 1;
                tensor[0, 1, y, x] = px[i + 1] / 127.5f - 1;
                tensor[0, 2, y, x] = px[i] / 127.5f - 1;
            }
        }

        return tensor;
    }

    private string Decode(IEnumerable<long> ids)
    {
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (id < 0 || id >= _tokens.Length || _special.Contains((int)id) || _tokens[id] is null)
            {
                continue;
            }

            foreach (var c in _tokens[id])
            {
                if (_bytes.TryGetValue(c, out var b))
                {
                    bytes.Add(b);
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                }
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    /// <summary>GPT-2's byte ↔ printable character table, reversed.</summary>
    private static Dictionary<char, byte> ByteDecoder()
    {
        var printable = new List<int>();
        printable.AddRange(Enumerable.Range('!', '~' - '!' + 1));
        printable.AddRange(Enumerable.Range('¡', '¬' - '¡' + 1));
        printable.AddRange(Enumerable.Range('®', 'ÿ' - '®' + 1));
        var map = new Dictionary<char, byte>();
        var extra = 0;
        for (var b = 0; b < 256; b++)
        {
            if (printable.Contains(b))
            {
                map[(char)b] = (byte)b;
            }
            else
            {
                map[(char)(256 + extra++)] = (byte)b;
            }
        }

        return map;
    }
}
