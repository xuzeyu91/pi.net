namespace Pi.Durable.Env;

/// <summary>
/// 可失败操作的结果。预期失败以值返回而不是抛出。对应 TS <c>env/index.ts</c> 的
/// <c>Result&lt;TValue, TError&gt;</c>（<c>{ ok: true; value }</c> | <c>{ ok: false; error }</c>）。
/// </summary>
public readonly struct Result<TValue, TError>
{
    private readonly TValue _value;
    private readonly TError _error;

    private Result(bool isOk, TValue value, TError error)
    {
        IsOk = isOk;
        _value = value;
        _error = error;
    }

    /// <summary>是否成功。为 true 时 <see cref="Value"/> 有效，否则 <see cref="Error"/> 有效。</summary>
    public bool IsOk { get; }

    /// <summary>成功时的值。</summary>
    public TValue Value => IsOk ? _value : throw new InvalidOperationException("Result is an error");

    /// <summary>失败时的错误。</summary>
    public TError Error => IsOk ? throw new InvalidOperationException("Result is a value") : _error;

    /// <summary>构造成功结果。对应 TS <c>ok(value)</c>。</summary>
    public static Result<TValue, TError> Ok(TValue value) => new(true, value, default!);

    /// <summary>构造失败结果。对应 TS <c>err(error)</c>。</summary>
    public static Result<TValue, TError> Err(TError error) => new(false, default!, error);

    /// <summary>成功时返回值，否则抛出错误。对应 TS <c>getOrThrow</c>。</summary>
    public TValue GetOrThrow()
    {
        if (!IsOk) throw _error as Exception ?? new InvalidOperationException(_error?.ToString());
        return _value;
    }

    /// <summary>成功时返回值，否则返回默认。对应 TS <c>getOrUndefined</c>（引用类型）。</summary>
    public TValue? GetOrUndefined() => IsOk ? _value : default;

    public static implicit operator Result<TValue, TError>(TValue value) => Ok(value);

    public void Deconstruct(out bool isOk, out TValue value, out TError error)
    {
        isOk = IsOk;
        value = _value;
        error = _error;
    }
}

/// <summary>把任意抛出的东西归一为 <see cref="Exception"/>。对应 TS <c>toError</c>。</summary>
public static class EnvErrors
{
    public static Exception ToError(object? error) => error as Exception
        ?? (error is string s ? new InvalidOperationException(s) : new InvalidOperationException(Describe(error)));

    private static string Describe(object? error)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(error);
        }
        catch
        {
            return error?.ToString() ?? "unknown error";
        }
    }
}
