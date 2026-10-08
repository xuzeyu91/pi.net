namespace Pi.CodingAgent.Utils;

/// <summary>
/// An error carrying a Node-style errno string (<c>ECONNREFUSED</c>, <c>ENOTFOUND</c>, …).
/// </summary>
/// <remarks>
/// Node exposes the errno as a <c>code</c> property on any error, which <c>formatVersionCheckError</c>
/// surfaces so a network failure is not reduced to the useless "fetch failed". .NET has no such
/// convention, so the port declares one: any exception implementing this interface contributes its
/// <see cref="Code"/> to that message.
/// </remarks>
public interface INodeError
{
    /// <summary>The Node-style error code, or <see langword="null"/> when there is none.</summary>
    string? Code { get; }
}

/// <summary>An <see cref="IOException"/> that also carries a Node-style errno code.</summary>
public class NodeIoException : IOException, INodeError
{
    /// <summary>Create the error.</summary>
    public NodeIoException(string message, string? code = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <inheritdoc />
    public string? Code { get; }
}
