using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Models;

/// <summary>
/// createProvider 的输入部件。对应 TS <c>CreateProviderOptions</c>（models.ts）。
/// </summary>
public sealed record CreateProviderOptions
{
    public required string Id { get; init; }

    /// <summary>显示名；缺省为 <see cref="Id"/>。</summary>
    public string? Name { get; init; }

    public string? BaseUrl { get; init; }

    /// <summary>必需——每个 provider 都有认证语义，即便纯环境源或无密钥本地服务器。</summary>
    public ProviderAuth? Auth { get; init; }

    /// <summary>静态基线模型（各类型；无 type 即 chat）。纯动态 provider 为空。</summary>
    public IReadOnlyList<ModelSpec> Models { get; init; } = [];

    /// <summary>单一 chat 实现（服务全部 chat 模型）。与 <see cref="ApiByApi"/> 二选一。</summary>
    public ProviderStreams? Api { get; init; }

    /// <summary>按 <c>model.api</c> 分派的 chat 实现映射（混合 API provider）。</summary>
    public IReadOnlyDictionary<string, ProviderStreams>? ApiByApi { get; init; }

    /// <summary>按 <c>model.api</c> 分派的图片生成实现。</summary>
    public IReadOnlyDictionary<string, ProviderImages>? Images { get; init; }

    /// <summary>按 <c>model.api</c> 分派的分类器实现。</summary>
    public IReadOnlyDictionary<string, ProviderClassifier>? Classifiers { get; init; }

    /// <summary>按凭据的 chat 模型可用性策略。对应 TS <c>filterModels</c>。</summary>
    public Func<IReadOnlyList<ModelSpec>, Credential?, IReadOnlyList<ModelSpec>>? FilterModels { get; init; }
}

/// <summary>
/// 从部件装配 provider。内建 provider 工厂与动态 provider 都经由此处。
/// 对应 TS <c>createProvider</c>（models.ts）：单 <c>api</c> 服务全部 chat 模型；
/// <c>api</c> 映射按 <c>model.api</c> 分派，缺条目产生 error 流；一次性操作映射同理；
/// <c>api</c>/<c>images</c>/<c>classifiers</c> 至少要有一个具体实现（空映射被拒绝）。
/// </summary>
public static class ProviderFactory
{
    public static IProvider Create(CreateProviderOptions input)
    {
        var single = input.Api;
        var byApi = single is null ? input.ApiByApi : null;
        var images = input.Images;
        var classifiers = input.Classifiers;

        var streams = single is not null
            ? [single]
            : (byApi?.Values ?? []).Where(entry => entry is not null).ToList();
        var imageImplementations = (images?.Values ?? []).Where(entry => entry is not null).ToList();
        var classifierImplementations = (classifiers?.Values ?? []).Where(entry => entry is not null).ToList();

        if (streams.Count == 0 && imageImplementations.Count == 0 && classifierImplementations.Count == 0)
        {
            throw new ArgumentException(
                $"Provider {input.Id}: at least one of \"api\", \"images\", or \"classifiers\" is required.");
        }

        return new CreatedProvider(input, single, byApi, images, classifiers, streams);
    }

    /// <summary>工厂产物：基线模型 + 按 api 分派的实现。</summary>
    private sealed class CreatedProvider : IProvider, IImagesProvider, IClassifierProvider
    {
        private readonly CreateProviderOptions _input;
        private readonly ProviderStreams? _single;
        private readonly IReadOnlyDictionary<string, ProviderStreams>? _byApi;
        private readonly IReadOnlyDictionary<string, ProviderImages>? _images;
        private readonly IReadOnlyDictionary<string, ProviderClassifier>? _classifiers;
        private readonly IReadOnlyList<ProviderStreams> _streams;

        public CreatedProvider(
            CreateProviderOptions input,
            ProviderStreams? single,
            IReadOnlyDictionary<string, ProviderStreams>? byApi,
            IReadOnlyDictionary<string, ProviderImages>? images,
            IReadOnlyDictionary<string, ProviderClassifier>? classifiers,
            IReadOnlyList<ProviderStreams> streams)
        {
            _input = input;
            _single = single;
            _byApi = byApi;
            _images = images;
            _classifiers = classifiers;
            _streams = streams;
        }

        public string Id => _input.Id;

        public string Name => _input.Name ?? _input.Id;

        public string? BaseUrl => _input.BaseUrl;

        public ProviderAuth? Auth => _input.Auth;

        public IReadOnlyList<ModelSpec> GetModels()
            => _input.Models.Where(model => model.Type == ModelType.Chat).ToList();

        public IReadOnlyList<ModelSpec> GetAllModels() => _input.Models;

        public IReadOnlyList<ModelSpec> FilterModels(IReadOnlyList<ModelSpec> models, Credential? credential)
            => _input.FilterModels?.Invoke(models, credential) ?? models;

        private ProviderStreams? ApiFor(ModelSpec model) => _single ?? _byApi?.GetValueOrDefault(model.Api);

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => Dispatch(model, context, options, streams => streams.Stream);

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => Dispatch(model, context, options, streams => streams.StreamSimple);

        private IAssistantMessageEventStream Dispatch(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options,
            Func<ProviderStreams, Func<ModelSpec, TranscriptContext, IReadOnlyDictionary<string, object?>?, IAssistantMessageEventStream>> select)
        {
            var streams = ApiFor(model);
            if (streams is null)
            {
                return LazyStream.Run(model, () => throw new ModelsError(ModelsErrorCode.Stream,
                    $"Provider {_input.Id} has no API implementation for \"{model.Api}\""));
            }
            return select(streams)(model, new TranscriptContext(context), options);
        }

        /// <summary>仅当任一实现导出延后方法时才暴露 <c>fetchDeferred</c>。对应 TS createProvider。</summary>
        public bool SupportsFetchDeferred => _streams.Any(entry => entry.FetchDeferred is not null);

        /// <summary>仅当任一实现导出取消方法时才暴露 <c>cancelDeferred</c>。对应 TS createProvider。</summary>
        public bool SupportsCancelDeferred => _streams.Any(entry => entry.CancelDeferred is not null);

        /// <summary>仅当任一实现导出延后方法时才暴露 <c>fetchDeferred</c>。对应 TS createProvider。</summary>
        public IAssistantMessageEventStream? StreamDeferred(ModelSpec model, DeferredHandle handle,
            IReadOnlyDictionary<string, object?>? options = null)
        {
            if (!SupportsFetchDeferred) return null;
            return LazyStream.Run(model, () =>
            {
                var implementation = ApiFor(model);
                if (implementation?.FetchDeferred is null)
                {
                    throw new ModelsError(ModelsErrorCode.Provider,
                        $"Provider {_input.Id} does not support deferred responses for \"{model.Api}\"");
                }
                return Task.FromResult(implementation.FetchDeferred(model, handle, options));
            });
        }

        public async Task CancelDeferredAsync(ModelSpec model, DeferredHandle handle,
            IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
        {
            if (!SupportsCancelDeferred)
            {
                throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} does not support deferred responses");
            }
            var implementation = ApiFor(model);
            if (implementation?.CancelDeferred is null)
            {
                throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} cannot cancel deferred responses for \"{model.Api}\"");
            }
            await implementation.CancelDeferred(model, handle, options, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
        {
            if (_images is null || _images.Count == 0)
            {
                return ModelOperations.ImageErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} does not support image generation"));
            }
            if (!_images.TryGetValue(model.Api, out var implementation) || implementation is null)
            {
                return ModelOperations.ImageErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} has no image generation implementation for \"{model.Api}\""));
            }
            return await implementation.GenerateImages(model, context, options, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
            ClassifierOptions? options, CancellationToken cancellationToken)
        {
            if (_classifiers is null || _classifiers.Count == 0)
            {
                return ModelOperations.ClassifierErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} does not support classification"));
            }
            if (!_classifiers.TryGetValue(model.Api, out var implementation) || implementation is null)
            {
                return ModelOperations.ClassifierErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} has no classifier implementation for \"{model.Api}\""));
            }
            return await implementation.Classify(model, context, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
