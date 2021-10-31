using System.Diagnostics.CodeAnalysis;

namespace Douze.Nodes;

internal abstract partial class NodeValue : Node
{
    /// <summary>
    ///   Tries to obtain the current JSON value and returns a value that indicates whether the operation succeeded.
    /// </summary>
    /// <seealso cref="Node.GetValue{T}"></seealso>
    /// <typeparam name="T">The type of value to obtain.</typeparam>
    /// <param name="value">When this method returns, contains the parsed value.</param>
    /// <returns><see langword="true"/> if the value can be successfully obtained; otherwise, <see langword="false"/>.</returns>
    public abstract bool TryGetValue<T>([NotNullWhen(true)] out T? value);
}
