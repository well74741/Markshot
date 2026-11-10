using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Markshot.Services;

/// <summary>
/// 离线翻译包: Marian (opus-mt) models in ONNX, downloaded on demand (never shipped with the app) into
/// %LOCALAPPDATA%\Markshot\translate\&lt;pack&gt;. A backup for when no large-model API is set up; Chinese ↔ English.
/// </summary>
public static class OfflinePacks
{
    public sealed record Pack(string Id, string Repo, string Label, string SourceLabel, string TargetLabel, string Prefix, long ApproxBytes);

    public static readonly Pack[] All =
    {
        new("en-zh", "Xenova/opus-mt-en-zh", "英文 → 中文", "English", "中文", ">>cmn_Hans<< ", 120_000_000),
        new("zh-en", "Xenova/opus-mt-zh-en", "中文 → 英文", "中文", "English", "", 120_000_000),
    };

    /// <summary>What is fetched (remote path, local name).</summary>
    public static readonly (string Remote, string Local)[] Files =
    {
        ("tokenizer.json", "tokenizer.json"),
        ("onnx/encoder_model_quantized.onnx", "encoder.onnx"),
        ("onnx/decoder_model_merged_quantized.onnx", "decoder.onnx"),
    };

    /// <summary>Download sources: Hugging Face itself, or its mirror (reachable where the first is not).</summary>
    public static readonly (string Id, string Label, string Host)[] Mirrors =
    {
        ("hf-mirror", "hf-mirror.com（国内镜像）", "https://hf-mirror.com"),
        ("huggingface", "huggingface.co", "https://huggingface.co"),
    };

    /// <summary>Tests: packs somewhere else.</summary>
    public static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Markshot", "translate");

    public static string Folder(Pack pack) => Path.Combine(Root, pack.Id);

    public static Pack? Find(string id) => All.FirstOrDefault(pack => pack.Id == id);

    public static bool IsInstalled(Pack pack) => Files.All(file => File.Exists(Path.Combine(Folder(pack), file.Local)));

    public static bool AnyInstalled => All.Any(IsInstalled);

    public static long SizeOnDisk(Pack pack)
    {
        var folder = Folder(pack);
        return Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles().Sum(file => file.Length) : 0;
    }

    /// <summary>The pack for a text into a target language (Chinese ↔ English), or null.</summary>
    public static Pack? For(string text, string targetLanguage)
    {
        var toChinese = targetLanguage.Contains('中') || targetLanguage.Contains("Chinese", StringComparison.OrdinalIgnoreCase) ||
                        targetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var toEnglish = targetLanguage.Contains('英') || targetLanguage.Contains("English", StringComparison.OrdinalIgnoreCase) ||
                        targetLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        if (toChinese)
        {
            return Find("en-zh");
        }

        return toEnglish ? Find("zh-en") : null;
    }

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Downloads a pack (each file to .part first; a stopped download leaves nothing half-done in use).</summary>
    public static async Task DownloadAsync(Pack pack, string mirrorId, IProgress<(long Done, long Total)>? progress, CancellationToken token)
    {
        await FetchAsync(pack.Repo, Files, Folder(pack), pack.ApproxBytes, mirrorId, progress, token);
        OfflineTranslator.Forget(pack.Id);
    }

    /// <summary>Files of a Hugging Face model into a folder (also used by the recognition model packs).</summary>
    internal static async Task FetchAsync(string repo, (string Remote, string Local)[] files, string folder, long total, string mirrorId,
        IProgress<(long Done, long Total)>? progress, CancellationToken token)
    {
        var host = Mirrors.FirstOrDefault(mirror => mirror.Id == mirrorId).Host ?? Mirrors[0].Host;
        Directory.CreateDirectory(folder);
        long done = 0;
        foreach (var (remote, local) in files)
        {
            var target = Path.Combine(folder, local);
            if (File.Exists(target))
            {
                done += new FileInfo(target).Length;
                continue;
            }

            var part = target + ".part";
            using var response = await Http.GetAsync($"{host}/{repo}/resolve/main/{remote}", HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(part))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    progress?.Report((done, Math.Max(total, done)));
                }
            }

            File.Move(part, target, overwrite: true);
        }

        progress?.Report((done, done));
    }

    public static void Delete(Pack pack)
    {
        OfflineTranslator.Forget(pack.Id);
        var folder = Folder(pack);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>One downloaded Marian pack: SentencePiece (unigram) pieces → encoder → greedy decoding with the cache.</summary>
public sealed class OfflineTranslator : IDisposable
{
    private static readonly Dictionary<string, OfflineTranslator> Loaded = new();
    private static readonly object Gate = new();

    private const int Eos = 0;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly Unigram _tokenizer;
    private readonly int _pad;
    private readonly string _prefix;
    private readonly object _run = new();

    private OfflineTranslator(OfflinePacks.Pack pack)
    {
        var folder = OfflinePacks.Folder(pack);
        var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        _encoder = new InferenceSession(Path.Combine(folder, "encoder.onnx"), options);
        _decoder = new InferenceSession(Path.Combine(folder, "decoder.onnx"), options);
        _tokenizer = Unigram.Load(Path.Combine(folder, "tokenizer.json"));
        _pad = _tokenizer.Id("<pad>") ?? 65000;
        _prefix = pack.Prefix;
    }

    public static OfflineTranslator Get(OfflinePacks.Pack pack)
    {
        lock (Gate)
        {
            if (!Loaded.TryGetValue(pack.Id, out var translator))
            {
                Loaded[pack.Id] = translator = new OfflineTranslator(pack);
            }

            return translator;
        }
    }

    /// <summary>A pack downloaded again / removed: its sessions go.</summary>
    public static void Forget(string id)
    {
        lock (Gate)
        {
            if (Loaded.Remove(id, out var translator))
            {
                translator.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }

    /// <summary>Translates line by line (each line one sentence for the model; blank lines stay).</summary>
    public string Translate(string text, CancellationToken token)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            token.ThrowIfCancellationRequested();
            result.Add(string.IsNullOrWhiteSpace(line) ? line : TranslateLine(line.Trim(), token));
        }

        return string.Join(Environment.NewLine, result);
    }

    private string TranslateLine(string line, CancellationToken token)
    {
        var ids = _tokenizer.Encode(_prefix + line);
        if (ids.Count > 500)
        {
            ids = ids.Take(500).ToList();
        }

        ids.Add(Eos);
        lock (_run)
        {
            var n = ids.Count;
            var inputIds = new DenseTensor<long>(ids.Select(id => (long)id).ToArray(), new[] { 1, n });
            var mask = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), new[] { 1, n });
            using var encoded = _encoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask)
            });
            var hidden = encoded.First().AsTensor<float>().ToDenseTensor();

            var past = new Dictionary<string, DenseTensor<float>>();
            foreach (var (name, meta) in _decoder.InputMetadata.Where(input => input.Key.StartsWith("past_key_values", StringComparison.Ordinal)))
            {
                var dims = meta.Dimensions.Select((d, i) => d > 0 ? d : i == 0 ? 1 : 0).ToArray();
                past[name] = new DenseTensor<float>(dims);
            }

            var output = new List<int>();
            var next = _pad;
            var limit = Math.Min(512, n * 3 + 10);
            for (var step = 0; step < limit; step++)
            {
                token.ThrowIfCancellationRequested();
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(new[] { (long)next }, new[] { 1, 1 })),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask)
                };
                if (_decoder.InputMetadata.ContainsKey("use_cache_branch"))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { step > 0 }, new[] { 1 })));
                }

                inputs.AddRange(past.Select(pair => NamedOnnxValue.CreateFromTensor(pair.Key, pair.Value)));
                using var results = _decoder.Run(inputs);
                var logits = results.First(result => result.Name == "logits").AsTensor<float>();
                var vocab = logits.Dimensions[2];
                var best = -1;
                var bestScore = float.NegativeInfinity;
                for (var v = 0; v < vocab; v++)
                {
                    if (v == _pad)
                    {
                        continue;
                    }

                    var score = logits[0, 0, v];
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = v;
                    }
                }

                if (best == Eos || best < 0)
                {
                    break;
                }

                output.Add(best);
                next = best;
                foreach (var result in results.Where(result => result.Name.StartsWith("present", StringComparison.Ordinal)))
                {
                    var name = "past_key_values" + result.Name["present".Length..];
                    // The encoder's part is fixed after the first step (the cached branch gives it back unchanged / empty).
                    if (step > 0 && name.EndsWith(".encoder.key", StringComparison.Ordinal) || step > 0 && name.EndsWith(".encoder.value", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    past[name] = result.AsTensor<float>().ToDenseTensor();
                }
            }

            return _tokenizer.Decode(output);
        }
    }

    /// <summary>The SentencePiece unigram model from tokenizer.json (Viterbi over its pieces; the ids are the model's).</summary>
    internal sealed class Unigram
    {
        private readonly Dictionary<string, (int Id, double Score)> _pieces = new();
        private readonly string[] _byId;
        private readonly HashSet<int> _special = new();
        private readonly int _unk;
        private readonly int _longest;

        private Unigram(List<(string Piece, double Score)> vocab, int unk, IEnumerable<int> special)
        {
            _byId = new string[vocab.Count];
            for (var i = 0; i < vocab.Count; i++)
            {
                _byId[i] = vocab[i].Piece;
                _pieces.TryAdd(vocab[i].Piece, (i, vocab[i].Score));
                _longest = Math.Max(_longest, vocab[i].Piece.Length);
            }

            _unk = unk;
            foreach (var id in special)
            {
                _special.Add(id);
            }
        }

        public static Unigram Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var model = doc.RootElement.GetProperty("model");
            var vocab = model.GetProperty("vocab").EnumerateArray()
                .Select(entry => (entry[0].GetString() ?? "", entry[1].GetDouble())).ToList();
            var unk = model.TryGetProperty("unk_id", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 1;
            var special = doc.RootElement.TryGetProperty("added_tokens", out var added)
                ? added.EnumerateArray().Where(t => t.GetProperty("special").GetBoolean()).Select(t => t.GetProperty("id").GetInt32()).ToList()
                : new List<int>();
            for (var i = 0; i < vocab.Count; i++)
            {
                if (vocab[i].Item1.StartsWith(">>", StringComparison.Ordinal) && vocab[i].Item1.EndsWith("<<", StringComparison.Ordinal))
                {
                    special.Add(i); // language tags
                }
            }

            return new Unigram(vocab, unk, special);
        }

        public int? Id(string piece) => _pieces.TryGetValue(piece, out var entry) ? entry.Id : null;

        /// <summary>Words (split at white space) as ▁word, each cut into the likeliest pieces; a language tag stays whole.</summary>
        public List<int> Encode(string text)
        {
            var ids = new List<int>();
            foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.StartsWith(">>", StringComparison.Ordinal) && _pieces.TryGetValue(word, out var tag))
                {
                    ids.Add(tag.Id);
                    continue;
                }

                ids.AddRange(Viterbi("▁" + word));
            }

            return ids;
        }

        private IEnumerable<int> Viterbi(string text)
        {
            var n = text.Length;
            var best = new double[n + 1];
            var from = new int[n + 1];
            var piece = new int[n + 1];
            Array.Fill(best, double.NegativeInfinity);
            best[0] = 0;
            for (var end = 1; end <= n; end++)
            {
                for (var start = Math.Max(0, end - _longest); start < end; start++)
                {
                    if (double.IsNegativeInfinity(best[start]))
                    {
                        continue;
                    }

                    if (_pieces.TryGetValue(text[start..end], out var entry) && !_special.Contains(entry.Id))
                    {
                        var score = best[start] + entry.Score;
                        if (score > best[end])
                        {
                            best[end] = score;
                            from[end] = start;
                            piece[end] = entry.Id;
                        }
                    }
                }

                if (double.IsNegativeInfinity(best[end]))
                {
                    // Unknown character: one <unk> for it.
                    best[end] = best[end - 1] - 20;
                    from[end] = end - 1;
                    piece[end] = _unk;
                }
            }

            var result = new List<int>();
            for (var at = n; at > 0; at = from[at])
            {
                result.Add(piece[at]);
            }

            result.Reverse();
            return result;
        }

        public string Decode(IEnumerable<int> ids)
        {
            var text = new StringBuilder();
            foreach (var id in ids)
            {
                if (id < 0 || id >= _byId.Length || _special.Contains(id) || id == _unk)
                {
                    continue;
                }

                text.Append(_byId[id]);
            }

            var plain = text.ToString().Replace('▁', ' ').Trim();
            // No spaces next to Chinese characters / full-width punctuation (the pieces put one before each word).
            return System.Text.RegularExpressions.Regex.Replace(plain, @"\s+(?=[　-〿㐀-鿿＀-￯])|(?<=[　-〿㐀-鿿＀-￯])\s+", "");
        }
    }
}

/// <summary>The offline packs as a translation provider (Chinese ↔ English).</summary>
public sealed class OfflineProvider : ITranslationProvider
{
    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var translator = Translator(text, targetLanguage);
        return await Task.Run(() => translator.Translate(text, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> segments, string targetLanguage, CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return Array.Empty<string>();
        }

        var translator = Translator(string.Join("\n", segments), targetLanguage);
        return await Task.Run(() => (IReadOnlyList<string>)segments
            .Select(segment => string.IsNullOrWhiteSpace(segment) ? segment : translator.Translate(segment, cancellationToken)).ToList(), cancellationToken);
    }

    private static OfflineTranslator Translator(string text, string targetLanguage)
    {
        var pack = OfflinePacks.For(text, targetLanguage)
                   ?? throw new InvalidOperationException("离线翻译包只支持中文 ↔ 英文，请把目标语言设为简体中文或 English");
        if (!OfflinePacks.IsInstalled(pack))
        {
            throw new InvalidOperationException($"还没有下载离线翻译包「{pack.Label}」：设置 → 翻译 → 离线翻译包");
        }

        return OfflineTranslator.Get(pack);
    }
}
