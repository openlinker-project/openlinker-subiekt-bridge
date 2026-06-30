namespace Subiekt.Bridge.Domain.Common;

/// <summary>
/// Outcome of an operation that can fail with a domain <see cref="Common.Error"/>.
/// Expected validation failures are modelled as values, not exceptions.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        // Guard the invariant: success carries no error, failure carries one.
        if (isSuccess && error != Error.None)
            throw new InvalidOperationException("A successful result cannot carry an error.");
        if (!isSuccess && error == Error.None)
            throw new InvalidOperationException("A failed result must carry an error.");

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);
}

/// <summary>
/// A <see cref="Result"/> that, on success, carries a <typeparamref name="T"/> value.
/// </summary>
public sealed class Result<T> : Result
{
    private readonly T _value;

    private Result(bool isSuccess, T value, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    /// <summary>
    /// The success value. Throws if accessed on a failed result — callers must
    /// check <see cref="Result.IsSuccess"/> first.
    /// </summary>
    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException("Cannot access the value of a failed result.");

    public static Result<T> Success(T value) => new(true, value, Error.None);

    public static new Result<T> Failure(Error error) => new(false, default!, error);
}
