using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using SmartSupportCRM.Application.Interfaces;
using SmartSupportCRM.Domain.Entities;
using SmartSupportCRM.Infrastructure.Data;
using SmartSupportCRM.Infrastructure.Services;
using SmartSupportCRM.Web.Models;
using Xunit;

namespace SmartSupportCRM.Tests;

public class AnalysisTests
{
    [Theory]
    [InlineData("Teşekkürler ama sistem çalışmıyor, acil yardım", SentimentType.Negative, TicketPriority.High, "Technical")]
    [InlineData("Harika, sorun çözüldü. Teşekkür ederim", SentimentType.Positive, TicketPriority.Medium, "General")]
    [InlineData("Fatura hakkında bilgi istiyorum", SentimentType.Neutral, TicketPriority.Medium, "Billing")]
    [InlineData("Thanks, but this is not resolved", SentimentType.Negative, TicketPriority.Medium, "General")]
    [InlineData("Veri kaybı var, sistem çöktü", SentimentType.Neutral, TicketPriority.Critical, "General")]
    [InlineData("The account is not good", SentimentType.Negative, TicketPriority.Medium, "Account")]
    public void RulesAreConservativeAndDoNotClaimConfidence(string text, SentimentType sentiment, TicketPriority priority, string category)
    {
        var result = TicketRules.Classify(text);
        Assert.Equal(sentiment, result.Sentiment);
        Assert.Equal(priority, result.Priority);
        Assert.Equal(category, result.Category);
        Assert.Equal(0, result.ConfidenceScore);
        Assert.Equal("Rules", result.Source);
    }

    [Fact]
    public void MissingModelUsesHonestFallback()
    {
        using var service = new OnnxSentimentInferenceService(Options.Create(new SentimentModelSettings
        {
            ModelPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx")
        }), NullLogger<OnnxSentimentInferenceService>.Instance);
        Assert.Equal("Rules", service.ClassifyTicket("Bu sistem çalışmıyor").Source);
        Assert.Throws<ArgumentException>(() => service.ClassifyTicket(" "));
    }

    [Fact]
    public void BertTokenizationSplitsSubwordsAndPreservesSpecialTokens()
    {
        using var vocab = new MemoryStream(Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nhelp\n##ful\n!\n"));
        var tokenizer = BertTokenizer.Create(vocab, new BertOptions());
        var encoded = tokenizer.EncodeToIds("helpful!", 3, false, out _, out _);
        Assert.Equal(new[] { 2, 5, 6, 7, 3 }, tokenizer.BuildInputsWithSpecialTokens(encoded));
    }

    [Theory]
    [InlineData("8")]
    [InlineData("sequence")]
    public void OnnxRunsWithAllBertInputsAndAppliesLabelMappingAndSoftmax(string shape)
    {
        var vocab = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(vocab, "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nhelp\n##ful\n!\n");
            using var service = new OnnxSentimentInferenceService(Options.Create(new SentimentModelSettings
            {
                ModelPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"constant-{shape}.onnx"),
                VocabPath = vocab,
                MaxSequenceLength = 8,
                Labels = ["Positive", "Negative", "Neutral"]
            }), NullLogger<OnnxSentimentInferenceService>.Instance);
            foreach (var input in new[] { "helpful!", string.Join(' ', Enumerable.Repeat("helpful!", 100)) })
            {
                var result = service.ClassifyTicket(input);
                Assert.Equal("ONNX", result.Source);
                Assert.Equal(SentimentType.Neutral, result.Sentiment);
                Assert.InRange(result.ConfidenceScore, 0.95f, 0.96f);
            }
        }
        finally { File.Delete(vocab); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return send(request, token);
        }
    }

    private static GeminiLlmService Service(Handler handler, string? key = "test-key", string? fallback = null) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gemini:ApiKey"] = key, ["Gemini:Model"] = "test-model", ["Gemini:FallbackModel"] = fallback
        }).Build(), new HttpClient(handler), NullLogger<GeminiLlmService>.Instance);

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Envelope(string output, string finish = "STOP") => JsonSerializer.Serialize(new
    {
        candidates = new[] { new { finishReason = finish, content = new { parts = new[] { new { text = output } } } } }
    });

    [Fact]
    public async Task GeminiUsesStructuredOutputAndSeparatesSummaryFromReply()
    {
        var handler = new Handler(async (request, cancellation) =>
        {
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("application/json", sent.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
            Assert.True(sent.RootElement.TryGetProperty("systemInstruction", out _));
            Assert.Equal("test-key", request.Headers.GetValues("x-goog-api-key").Single());
            return JsonResponse(Envelope("{\"summary\":\"Ödeme hatası\",\"suggestedReply\":\"Hata mesajını paylaşır mısınız?\",\"sentiment\":\"Negative\",\"priority\":\"High\",\"category\":\"Billing\"}"));
        });
        var result = await Service(handler).AnalyzeAndDraftReplyAsync("Ödeme yapamıyorum");
        Assert.Equal("Ödeme hatası", result.Summary);
        Assert.Equal("Hata mesajını paylaşır mısınız?", result.SuggestedDraft);
        Assert.Equal(SentimentType.Negative, result.Sentiment);
        Assert.Equal(TicketPriority.High, result.Priority);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"SAFETY\"}]}")]
    [InlineData("not json")]
    public async Task InvalidOrBlockedResponsesDoNotBecomeCustomerDrafts(string body)
    {
        var handler = new Handler((_, _) => Task.FromResult(JsonResponse(body)));
        var result = await Service(handler).AnalyzeAndDraftReplyAsync("Test ticket");
        Assert.Null(result.Summary);
        Assert.Null(result.SuggestedDraft);
    }

    [Fact]
    public async Task TruncatedCandidateIsRejected()
    {
        var handler = new Handler((_, _) => Task.FromResult(JsonResponse(Envelope("{}", "MAX_TOKENS"))));
        Assert.Null((await Service(handler).AnalyzeAndDraftReplyAsync("Test")).Summary);
    }

    [Fact]
    public async Task MissingKeyDoesNotCallProvider()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException());
        Assert.Null((await Service(handler, "").AnalyzeAndDraftReplyAsync("Test")).Summary);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnauthorizedResponseIsNotRetried()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.Null((await Service(handler).AnalyzeAndDraftReplyAsync("Test")).Summary);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TransientFailureIsReturnedToPersistentQueueWithoutNestedRetries()
    {
        var count = 0;
        var handler = new Handler((_, _) =>
        {
            count++;
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        var result = await Service(handler).AnalyzeAndDraftReplyAsync("Test");
        Assert.Null(result.Summary);
        Assert.Equal(AnalysisErrorCode.ServiceUnavailable, result.ErrorCode);
        Assert.True(result.CanRetry);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        var handler = new Handler((_, token) => Task.FromCanceled<HttpResponseMessage>(token));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(handler).AnalyzeAndDraftReplyAsync("Test", cancellation.Token));
    }

    [Fact]
    public async Task UnavailableModelCanUseOnlyConfiguredSecondLlm()
    {
        var calls = 0;
        var handler = new Handler((request, _) =>
        {
            if (calls++ == 0) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            Assert.Contains("backup-model", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse(Envelope("{\"summary\":\"Negative experience\",\"suggestedReply\":\"Please share details\",\"sentiment\":\"Negative\",\"priority\":\"Medium\",\"category\":\"General\"}")));
        });
        var result = await Service(handler, fallback: "backup-model").AnalyzeAndDraftReplyAsync("Test");
        Assert.True(result.Succeeded);
        Assert.Equal("backup-model", result.Model);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task ModelSwitchingDoesNotMaskConfigurationOrBypassQuota(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        Assert.False((await Service(handler, fallback: "backup-model").AnalyzeAndDraftReplyAsync("Test")).Succeeded);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task QuotaResponsePreservesRetryInfoInsteadOfPretendingAnalysisSucceeded()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"details\":[{\"retryDelay\":\"91.5s\"}]}}")
        }));
        var result = await Service(handler).AnalyzeAndDraftReplyAsync("Test");
        Assert.False(result.Succeeded);
        Assert.True(result.CanRetry);
        Assert.Equal(AnalysisErrorCode.QuotaExceeded, result.ErrorCode);
        Assert.Equal(92, result.RetryAfterSeconds);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DailyQuotaIsNotRetriedEveryMinute()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"details\":[{\"violations\":[{\"quotaId\":\"GenerateRequestsPerDay\"}]}]}}")
        }));
        Assert.Equal(86400, (await Service(handler).AnalyzeAndDraftReplyAsync("Test")).RetryAfterSeconds);
    }

    [Theory]
    [InlineData(401, AnalysisErrorCode.Authentication)]
    [InlineData(403, AnalysisErrorCode.PermissionDenied)]
    [InlineData(404, AnalysisErrorCode.ModelUnavailable)]
    [InlineData(503, AnalysisErrorCode.ServiceUnavailable)]
    public async Task ProviderErrorsAreClassified(int status, AnalysisErrorCode expected)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        Assert.Equal(expected, (await Service(handler).AnalyzeAndDraftReplyAsync("Test")).ErrorCode);
    }

    [Fact]
    public async Task InvalidKeySentAsBadRequestIsRecognized()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}")
        }));
        var result = await Service(handler).AnalyzeAndDraftReplyAsync("Test");
        Assert.Equal(AnalysisErrorCode.Authentication, result.ErrorCode);
        Assert.False(result.CanRetry);
    }

    [Fact]
    public async Task TimeoutIsDistinguishedFromBadCredentials()
    {
        var handler = new Handler((_, _) => throw new TaskCanceledException());
        var result = await Service(handler).AnalyzeAndDraftReplyAsync("Test");
        Assert.Equal(AnalysisErrorCode.Timeout, result.ErrorCode);
        Assert.True(result.CanRetry);
    }

    [Theory]
    [InlineData("", "Subject", "Content")]
    [InlineData("invalid", "Subject", "Content")]
    [InlineData("a@example.com", " ", "Content")]
    [InlineData("a@example.com", "Subject", " ")]
    public void InvalidTicketInputIsRejected(string email, string subject, string content)
    {
        var input = new CreateTicketViewModel { CustomerEmail = email, Subject = subject, Content = content };
        Assert.False(Validator.TryValidateObject(input, new ValidationContext(input), [], true));
    }

}
