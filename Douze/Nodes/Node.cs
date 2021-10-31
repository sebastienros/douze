namespace Douze.Nodes;

internal abstract partial class Node : IEquatable<Node>
{
    /// <summary>
    ///   Casts to the derived <see cref="NodeArray"/> type.
    /// </summary>
    /// <returns>
    ///   A <see cref="NodeArray"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///   The node is not a <see cref="NodeArray"/>.
    /// </exception>
    public NodeArray AsArray()
    {
        if (this is NodeArray jArray)
        {
            return jArray;
        }

        throw new InvalidOperationException("Not a NodeArray");
    }

    /// <summary>
    ///   Casts to the derived <see cref="NodeObject"/> type.
    /// </summary>
    /// <returns>
    ///   A <see cref="JsonObject"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///   The node is not a <see cref="NodeObject"/>.
    /// </exception>
    public NodeObject AsObject()
    {
        if (this is NodeObject jObject)
        {
            return jObject;
        }

        throw new InvalidOperationException("Not a NodeObject");
    }

    /// <summary>
    ///   Casts to the derived <see cref="NodeValue"/> type.
    /// </summary>
    /// <returns>
    ///   A <see cref="NodeValue"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///   The node is not a <see cref="NodeValue"/>.
    /// </exception>
    public NodeValue AsValue()
    {
        if (this is NodeValue jValue)
        {
            return jValue;
        }

        throw new InvalidOperationException("Not a NodeValue");
    }

    /// <summary>
    ///   Gets or sets the element at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the element to get or set.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///   <paramref name="index"/> is less than 0 or <paramref name="index"/> is greater than the number of properties.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///   The current <see cref="Node"/> is not a <see cref="NodeArray"/>.
    /// </exception>
    public Node? this[int index]
    {
        get
        {
            return AsArray().GetItem(index);
        }
        set
        {
            AsArray().SetItem(index, value);
        }
    }

    /// <summary>
    ///   Gets or sets the element with the specified property name.
    ///   If the property is not found, <see langword="null"/> is returned.
    /// </summary>
    /// <param name="propertyName">The name of the property to return.</param>
    /// <exception cref="ArgumentNullException">
    ///   <paramref name="propertyName"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///   The current <see cref="Node"/> is not a <see cref="JsonObject"/>.
    /// </exception>
    public Node? this[string propertyName]
    {
        get
        {
            return AsObject().GetItem(propertyName);
        }
        set
        {
            AsObject().SetItem(propertyName, value);
        }
    }

    public abstract T GetValue<T>();

    public abstract Node Clone();

    public abstract bool Equals(Node? other);
}
