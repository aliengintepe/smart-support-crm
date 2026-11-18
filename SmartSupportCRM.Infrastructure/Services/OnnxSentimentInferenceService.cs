using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SmartSupportCRM.Application.Interfaces;
using SmartSupportCRM.Domain.Entities;

namespace SmartSupportCRM.Infrastructure.Services;

public class SentimentModelSettings
{
    public string ModelPath { get; set; } = "Models/sentiment_model.onnx";
    public string VocabPath { get; set; } = "Models/vocab.txt";
    // Must match tokenizer_config.json and config.json, including label order.
    public bool LowerCase { get; set; } = true;
    public bool RemoveNonSpacingMarks { get; set; } = true;
    public int MaxSequenceLength { get; set; } = 512;
    public string[] Labels { get; set; } = [];
    public string OutputName { get; set; } = "logits";
}

public sealed class OnnxSentimentInferenceService : IInferenceService, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly BertTokenizer? _tokenizer;
    private readonly SentimentModelSettings _settings;
    private readonly ILogger<OnnxSentimentInferenceService> _logger;

    public OnnxSentimentInferenceService(IOptions<SentimentModelSettings> settings,
        ILogger<OnnxSentimentInferenceService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        InferenceSession? session = null;
        try
        {
            var model = Resolve(_settings.ModelPath);
            var vocab = Resolve(_settings.VocabPath);
            if (!File.Exists(model) || new FileInfo(model).Length < 1024 || !File.Exists(vocab))
                throw new InvalidOperationException("Real ONNX model and matching vocabulary are required.");
            if (_settings.MaxSequenceLength is < 3 or > 512 || _settings.Labels.Length is < 2 or > 3 ||
                _settings.Labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != _settings.Labels.Length ||
                _settings.Labels.Any(l => !Enum.TryParse<SentimentType>(l, true, out var s) || !Enum.IsDefined(s)))
                throw new InvalidOperationException("Configure sequence length and explicit Positive/Neutral/Negative label order from model config.");
            var vocabulary = File.ReadAllLines(vocab);
            if (new[] { "[PAD]", "[UNK]", "[CLS]", "[SEP]" }.Any(t => !vocabulary.Contains(t)))
                throw new InvalidOperationException("Vocabulary does not contain required BERT special tokens.");
            var tokenizer = BertTokenizer.Create(vocab, new BertOptions
            {
                LowerCaseBeforeTokenization = _settings.LowerCase,
                RemoveNonSpacingMarks = _settings.RemoveNonSpacingMarks
            });
            session = new InferenceSession(model);
            if (!session.InputMetadata.ContainsKey("input_ids") ||
                session.InputMetadata.Any(i => i.Key is not ("input_ids" or "attention_mask" or "token_type_ids") ||
                    i.Value.ElementType != typeof(long) || i.Value.Dimensions.Length != 2 || i.Value.Dimensions[0] > 1) ||
                !session.OutputMetadata.TryGetValue(_settings.OutputName, out var output) ||
                output.ElementType != typeof(float) || output.Dimensions.Length != 2 ||
                (output.Dimensions[1] > 0 && output.Dimensions[1] != _settings.Labels.Length))
                throw new InvalidOperationException("Expected BERT int64 inputs and float [1, labels] logits output.");
            var lengths = session.InputMetadata.Values.Select(m => m.Dimensions[1]).Where(n => n > 0).Distinct().ToArray();
            if (lengths.Length > 1 || lengths.Any(n => n < 3 || n > _settings.MaxSequenceLength))
                throw new InvalidOperationException("Model input lengths do not match configured sequence length.");
            _tokenizer = tokenizer;
            _session = session;
            _logger.LogInformation("ONNX sentiment model loaded from {Path}", model);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            _logger.LogWarning(ex, "ONNX unavailable. Rule-based estimates have no model confidence.");
        }
    }

    private static string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    public SentimentAnalysisResult ClassifyTicket(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var rules = TicketRules.Classify(text);
        if (_session == null || _tokenizer == null) return rules;
        try
        {
            var fixedLength = _session.InputMetadata.Values.Select(m => m.Dimensions[1]).FirstOrDefault(n => n > 0);
            var maxLength = fixedLength > 0 ? fixedLength : _settings.MaxSequenceLength;
            // Reserve CLS and SEP and always preserve SEP after truncation.
            var encoded = _tokenizer.EncodeToIds(text, maxLength - 2, false, out _, out _);
            var tokens = _tokenizer.BuildInputsWithSpecialTokens(encoded).Select(n => (long)n).ToArray();
            var length = fixedLength > 0 ? fixedLength : tokens.Length;
            var ids = Enumerable.Repeat((long)_tokenizer.PaddingTokenId, length).ToArray();
            Array.Copy(tokens, ids, tokens.Length);
            var mask = Enumerable.Range(0, length).Select(i => i < tokens.Length ? 1L : 0L).ToArray();
            var inputs = _session.InputMetadata.Select(input => NamedOnnxValue.CreateFromTensor(input.Key,
                new DenseTensor<long>(input.Key switch
                {
                    "input_ids" => ids,
                    "attention_mask" => mask,
                    _ => new long[length]
                }, [1, length]))).ToArray();
            using var results = _session.Run(inputs);
            var tensor = results.Single(r => r.Name == _settings.OutputName).AsTensor<float>();
            var logits = tensor.ToArray();
            if (tensor.Dimensions.Length != 2 || tensor.Dimensions[0] != 1 ||
                logits.Length != _settings.Labels.Length || logits.Any(v => !float.IsFinite(v)))
                throw new InvalidOperationException("Invalid sentiment logits.");
            var max = logits.Max();
            var probabilities = logits.Select(v => Math.Exp(v - max)).ToArray();
            var best = Array.IndexOf(logits, max);
            return rules with
            {
                Sentiment = Enum.Parse<SentimentType>(_settings.Labels[best], true),
                ConfidenceScore = (float)(probabilities[best] / probabilities.Sum()),
                Source = "ONNX"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ONNX inference failed; using rule-based estimate.");
            return rules;
        }
    }

    public void Dispose() => _session?.Dispose();
}
