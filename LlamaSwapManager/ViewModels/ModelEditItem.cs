using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using LlamaSwapManager.Models;

namespace LlamaSwapManager.ViewModels;

public partial class ModelEditItem : ObservableObject
{
    [ObservableProperty] private string _modelId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _llamaServerPath = "";
    [ObservableProperty] private string _hfModel = "";
    [ObservableProperty] private string _modelPath = "";
    [ObservableProperty] private string _selectedQuantization = "";
    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private string _port = "${PORT}";
    [ObservableProperty] private string _temperature = "0.2";
    [ObservableProperty] private string _topK = "80";
    [ObservableProperty] private string _repeatPenalty = "1.0";
    [ObservableProperty] private string _presencePenalty = "0";
    [ObservableProperty] private string _contextSize = "";
    [ObservableProperty] private string _predict = "";
    [ObservableProperty] private string _batchSize = "";
    [ObservableProperty] private string _uBatchSize = "";
    [ObservableProperty] private string _threads = "";
    [ObservableProperty] private string _threadsBatch = "";
    [ObservableProperty] private string _gpuLayers = "";
    [ObservableProperty] private string _device = "";
    [ObservableProperty] private string _splitMode = "";
    [ObservableProperty] private string _tensorSplit = "";
    [ObservableProperty] private string _mainGpu = "";
    [ObservableProperty] private string _flashAttention = "";
    [ObservableProperty] private string _cacheTypeK = "";
    [ObservableProperty] private string _cacheTypeV = "";
    [ObservableProperty] private bool _mlock;
    [ObservableProperty] private string _topP = "";
    [ObservableProperty] private string _minP = "";
    [ObservableProperty] private string _frequencyPenalty = "";
    [ObservableProperty] private string _repeatLastN = "";
    [ObservableProperty] private string _seed = "";
    [ObservableProperty] private string _samplers = "";
    [ObservableProperty] private string _parallel = "";
    [ObservableProperty] private bool _contBatching = true;
    [ObservableProperty] private bool _embeddings;
    [ObservableProperty] private bool _reranking;
    [ObservableProperty] private bool _metrics = true;
    [ObservableProperty] private bool _propsEndpoint;
    [ObservableProperty] private bool _slots = true;
    [ObservableProperty] private string _timeout = "";
    [ObservableProperty] private string _threadsHttp = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _chatTemplate = "";
    [ObservableProperty] private string _reasoning = "";
    [ObservableProperty] private string _reasoningFormat = "";
    [ObservableProperty] private string _reasoningBudget = "";
    [ObservableProperty] private string _extraArgs = "";
    [ObservableProperty] private bool _useJinja;
    [ObservableProperty] private bool _fitOn;
    [ObservableProperty] private bool _noMmap;
    [ObservableProperty] private string _ttl = "0";
    [ObservableProperty] private string _aliasesText = "";
    [ObservableProperty] private bool _isNew = false;
    [ObservableProperty] private bool _isSelected = false;

    public string CmdPreview
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ModelPath))
                return $"-m {Path.GetFileName(ModelPath)} --temp {Temperature}";
            if (!string.IsNullOrWhiteSpace(HfModel))
            {
                var q = !string.IsNullOrWhiteSpace(SelectedQuantization) ? $" ({SelectedQuantization})" : "";
                return $"--hf {HfModel}{q} --temp {Temperature}";
            }
            return $"--temp {Temperature}";
        }
    }

    public ModelEditItem CloneAs(string newModelId)
    {
        return new ModelEditItem
        {
            ModelId = newModelId,
            Name = string.IsNullOrWhiteSpace(Name) ? newModelId : $"{Name} copy",
            Description = Description,
            AliasesText = "",
            LlamaServerPath = LlamaServerPath,
            ModelPath = ModelPath,
            SelectedQuantization = SelectedQuantization,
            HfModel = HfModel,
            Host = Host,
            Port = Port,
            UseJinja = UseJinja,
            ContextSize = ContextSize,
            Predict = Predict,
            Threads = Threads,
            ThreadsBatch = ThreadsBatch,
            BatchSize = BatchSize,
            UBatchSize = UBatchSize,
            GpuLayers = GpuLayers,
            Device = Device,
            SplitMode = SplitMode,
            TensorSplit = TensorSplit,
            MainGpu = MainGpu,
            FlashAttention = FlashAttention,
            FitOn = FitOn,
            NoMmap = NoMmap,
            Mlock = Mlock,
            CacheTypeK = CacheTypeK,
            CacheTypeV = CacheTypeV,
            Temperature = Temperature,
            TopK = TopK,
            TopP = TopP,
            MinP = MinP,
            RepeatPenalty = RepeatPenalty,
            PresencePenalty = PresencePenalty,
            FrequencyPenalty = FrequencyPenalty,
            RepeatLastN = RepeatLastN,
            Seed = Seed,
            Samplers = Samplers,
            Parallel = Parallel,
            ContBatching = ContBatching,
            Embeddings = Embeddings,
            Reranking = Reranking,
            Metrics = Metrics,
            PropsEndpoint = PropsEndpoint,
            Slots = Slots,
            Timeout = Timeout,
            ThreadsHttp = ThreadsHttp,
            ApiKey = ApiKey,
            ChatTemplate = ChatTemplate,
            Reasoning = Reasoning,
            ReasoningFormat = ReasoningFormat,
            ReasoningBudget = ReasoningBudget,
            ExtraArgs = ExtraArgs,
            Ttl = Ttl,
            IsNew = true
        };
    }

    public static ModelEditItem Parse(string modelId, ModelConfig config)
    {
        var item = new ModelEditItem
        {
            ModelId = modelId,
            Name = config.Name ?? modelId,
            Description = config.Description ?? "",
            UseJinja = config.Cmd?.Contains("--jinja") ?? false,
            FitOn = config.Cmd?.Contains("--fit on") ?? false,
            NoMmap = config.Cmd?.Contains("--no-mmap") ?? false,
            Ttl = config.Ttl?.ToString() ?? "0",
            AliasesText = (config.Aliases ?? new List<string>())
                   .Where(a => !string.IsNullOrEmpty(a))
                   .ToList() is var aliases && aliases.Count > 0
                   ? string.Join(", ", aliases)
                   : ""
        };

        if (!string.IsNullOrEmpty(config.Cmd))
        {
            var parts = TokenizeCommand(config.Cmd);
            if (parts.Count > 0)
                item.LlamaServerPath = parts[0];

            var extras = new List<string>();
            for (var i = 1; i < parts.Count; i++)
            {
                var part = parts[i];
                string? Next() => i + 1 < parts.Count ? Unquote(parts[++i]) : null;
                bool Match(params string[] names) => names.Contains(part);

                if (Match("--jinja")) item.UseJinja = true;
                else if (Match("--no-jinja")) item.UseJinja = false;
                else if (Match("--fit", "-fit")) item.FitOn = (Next() ?? "on") == "on";
                else if (Match("--no-mmap")) item.NoMmap = true;
                else if (Match("--mmap")) item.NoMmap = false;
                else if (Match("--mlock")) item.Mlock = true;
                else if (Match("-m", "--model")) item.ModelPath = Next() ?? "";
                else if (Match("-hf", "-hfr", "--hf", "--hf-repo"))
                {
                    var hfArg = Next() ?? "";
                    var colonIdx = hfArg.LastIndexOf(':');
                    if (colonIdx > 0)
                    {

                        item.HfModel = hfArg[..colonIdx];
                        item.SelectedQuantization = hfArg[(colonIdx + 1)..];
                    }
                    else
                    {
                        item.HfModel = hfArg;
                        item.SelectedQuantization = "";
                    }
                }
                else if (Match("--host")) item.Host = Next() ?? item.Host;
                else if (Match("--port")) item.Port = Next() ?? item.Port;
                else if (Match("--temp", "--temperature")) item.Temperature = Next() ?? item.Temperature;
                else if (Match("--top-k")) item.TopK = Next() ?? item.TopK;
                else if (Match("--top-p")) item.TopP = Next() ?? "";
                else if (Match("--min-p")) item.MinP = Next() ?? "";
                else if (Match("--repeat-penalty")) item.RepeatPenalty = Next() ?? item.RepeatPenalty;
                else if (Match("--presence-penalty")) item.PresencePenalty = Next() ?? item.PresencePenalty;
                else if (Match("--frequency-penalty")) item.FrequencyPenalty = Next() ?? "";
                else if (Match("--repeat-last-n")) item.RepeatLastN = Next() ?? "";
                else if (Match("-s", "--seed")) item.Seed = Next() ?? "";
                else if (Match("--samplers")) item.Samplers = Next() ?? "";
                else if (Match("--ctx-size", "-c")) item.ContextSize = Next() ?? "";
                else if (Match("--predict", "--n-predict", "-n")) item.Predict = Next() ?? "";
                else if (Match("--batch-size", "-b")) item.BatchSize = Next() ?? "";
                else if (Match("--ubatch-size", "-ub")) item.UBatchSize = Next() ?? "";
                else if (Match("--threads", "-t")) item.Threads = Next() ?? "";
                else if (Match("--threads-batch", "-tb")) item.ThreadsBatch = Next() ?? "";
                else if (Match("--gpu-layers", "--n-gpu-layers", "-ngl")) item.GpuLayers = Next() ?? "";
                else if (Match("--device", "-dev")) item.Device = Next() ?? "";
                else if (Match("--split-mode", "-sm")) item.SplitMode = Next() ?? "";
                else if (Match("--tensor-split", "-ts")) item.TensorSplit = Next() ?? "";
                else if (Match("--main-gpu", "-mg")) item.MainGpu = Next() ?? "";
                else if (Match("--flash-attn", "-fa")) item.FlashAttention = Next() ?? "";
                else if (Match("--cache-type-k", "-ctk")) item.CacheTypeK = Next() ?? "";
                else if (Match("--cache-type-v", "-ctv")) item.CacheTypeV = Next() ?? "";
                else if (Match("--parallel", "-np")) item.Parallel = Next() ?? "";
                else if (Match("--cont-batching", "-cb")) item.ContBatching = true;
                else if (Match("--no-cont-batching", "-nocb")) item.ContBatching = false;
                else if (Match("--embedding", "--embeddings")) item.Embeddings = true;
                else if (Match("--rerank", "--reranking")) item.Reranking = true;
                else if (Match("--metrics")) item.Metrics = true;
                else if (Match("--props")) item.PropsEndpoint = true;
                else if (Match("--slots")) item.Slots = true;
                else if (Match("--no-slots")) item.Slots = false;
                else if (Match("--timeout", "-to")) item.Timeout = Next() ?? "";
                else if (Match("--threads-http")) item.ThreadsHttp = Next() ?? "";
                else if (Match("--api-key")) item.ApiKey = Next() ?? "";
                else if (Match("--chat-template")) item.ChatTemplate = Next() ?? "";
                else if (Match("--reasoning", "-rea")) item.Reasoning = Next() ?? "";
                else if (Match("--reasoning-format")) item.ReasoningFormat = Next() ?? "";
                else if (Match("--reasoning-budget")) item.ReasoningBudget = Next() ?? "";
                else extras.Add(part);
            }
            item.ExtraArgs = string.Join(" ", extras);
        }

        return item;
    }

    public string BuildCmd()
    {
        var parts = new List<string>();
        void AddValue(string flag, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{flag} {QuoteIfNeeded(value.Trim())}");
        }

        static string? NormalizeOptional(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var t = value.Trim();
            // UI may show "(default)" for omit; never emit that token to the CLI.
            if (t.Equals("(default)", StringComparison.OrdinalIgnoreCase)) return null;
            return t;
        }

        if (!string.IsNullOrWhiteSpace(LlamaServerPath)) parts.Add(QuoteIfNeeded(LlamaServerPath.Trim()));
        if (!string.IsNullOrWhiteSpace(ModelPath)) AddValue("-m", ModelPath);
        else if (!string.IsNullOrWhiteSpace(HfModel))
        {
            var hfArg = SelectedQuantization != null && SelectedQuantization.Contains('/')
                ? HfModel + ":" + SelectedQuantization
                : (string.IsNullOrEmpty(SelectedQuantization) ? HfModel : HfModel + ":" + SelectedQuantization);
            AddValue("-hf", hfArg);
        }

        AddValue("--host", Host);
        AddValue("--port", Port);
        AddValue("--threads", Threads);
        AddValue("--threads-batch", ThreadsBatch);
        AddValue("--ctx-size", ContextSize);
        AddValue("--predict", Predict);
        AddValue("--batch-size", BatchSize);
        AddValue("--ubatch-size", UBatchSize);
        AddValue("--gpu-layers", NormalizeOptional(GpuLayers));
        AddValue("--device", Device);
        AddValue("--split-mode", SplitMode);
        AddValue("--tensor-split", TensorSplit);
        AddValue("--main-gpu", MainGpu);
        AddValue("--flash-attn", FlashAttention);
        AddValue("--cache-type-k", CacheTypeK);
        AddValue("--cache-type-v", CacheTypeV);
        if (Mlock) parts.Add("--mlock");
        if (NoMmap) parts.Add("--no-mmap");
        if (FitOn) parts.Add("--fit on");

        AddValue("--temp", Temperature);
        AddValue("--top-k", TopK);
        AddValue("--top-p", TopP);
        AddValue("--min-p", MinP);
        AddValue("--repeat-penalty", RepeatPenalty);
        AddValue("--presence-penalty", PresencePenalty);
        AddValue("--frequency-penalty", FrequencyPenalty);
        AddValue("--repeat-last-n", RepeatLastN);
        AddValue("--seed", Seed);
        AddValue("--samplers", Samplers);

        AddValue("--parallel", Parallel);
        if (!ContBatching) parts.Add("--no-cont-batching");
        if (Embeddings) parts.Add("--embeddings");
        if (Reranking) parts.Add("--reranking");
        if (Metrics) parts.Add("--metrics");
        if (PropsEndpoint) parts.Add("--props");
        if (!Slots) parts.Add("--no-slots");
        AddValue("--timeout", Timeout);
        AddValue("--threads-http", ThreadsHttp);
        AddValue("--api-key", ApiKey);

        if (UseJinja) parts.Add("--jinja");
        AddValue("--chat-template", NormalizeOptional(ChatTemplate));
        // Critical: "off" must always emit --reasoning off (not omit the flag).
        AddValue("--reasoning", NormalizeOptional(Reasoning));
        AddValue("--reasoning-format", NormalizeOptional(ReasoningFormat));
        AddValue("--reasoning-budget", NormalizeOptional(ReasoningBudget));

        if (!string.IsNullOrWhiteSpace(ExtraArgs)) parts.Add(ExtraArgs.Trim());
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static List<string> TokenizeCommand(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (ch == '"' && (i == 0 || command[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
                current.Append(ch);
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
            return value[1..^1].Replace("\\\"", "\"");
        return value;
    }

    private static string QuoteIfNeeded(string value)
    {
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }
}
