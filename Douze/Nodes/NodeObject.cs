using System.Diagnostics;

namespace Douze.Nodes;

internal partial class NodeObject : Node
{
    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeObject"/> class that is empty.
    /// </summary>
    public NodeObject() { }

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeObject"/> class that contains the specified <paramref name="properties"/>.
    /// </summary>
    /// <param name="properties">The properties to be added.</param>
    /// <param name="options">Options to control the behavior.</param>
    public NodeObject(IEnumerable<KeyValuePair<string, Node?>> properties)
    {
        foreach (KeyValuePair<string, Node?> node in properties)
        {
            Add(node.Key, node.Value);
        }
    }

    internal Node? GetItem(string propertyName)
    {
        if (_dictionary != null && _dictionary.TryGetValue(propertyName, out var value))
        {
            return value;
        }

        // Return null for missing properties.
        return null;
    }

    internal void SetItem(string propertyName, Node? value)
    {
        InitializeIfRequired();
        Debug.Assert(_dictionary != null);
        _dictionary[propertyName] = value;
    }

    public override T GetValue<T>()
    {
        throw new NotImplementedException();
    }

    public override Node Clone()
    {
        var obj = new NodeObject();

        if (_dictionary == null)
        {
            return obj;
        }

        foreach (var item in _dictionary)
        {
            obj.Add(item.Key, item.Value?.Clone());
        }

        return obj;
    }

    public void Merge(NodeObject node, MergeSettings? mergeSettings = null)
    {
        mergeSettings ??= MergeSettings.Default;

        foreach (var property in node)
        {
            var localNode = GetItem(property.Key);

            if (property.Value is NodeValue value)
            {
                // If the current entry is an array and the merged node a value, use array merge
                var localArray = localNode as NodeArray;

                if (localArray != null)
                {
                    localArray.Merge(new NodeArray(value), mergeSettings);
                }
                else
                {
                    // Otherwise replace value
                    SetItem(property.Key, value);
                }
            }
            else if (property.Value is NodeArray array)
            {
                // If the current entry is a value and the merged node an array, use array merge
                var localValue = localNode as NodeValue;

                if (localValue != null)
                {
                    var newArray = new NodeArray(localValue);
                    newArray.Merge(array, mergeSettings);

                    SetItem(property.Key, newArray);
                }
                else
                {
                    // If the current entry is an array value and the merged node an array, use array merge
                    var localArray = localNode as NodeArray;

                    if (localArray != null)
                    {
                        localArray.Merge(array, mergeSettings);
                    }
                    else
                    {
                        // Otherwise replace value
                        SetItem(property.Key, array);
                    }
                }
            }
            else if (property.Value is NodeObject obj)
            {
                if (localNode is NodeObject localObject)
                {
                    localObject.Merge(obj, mergeSettings);
                }
                else
                {
                    // Otherwise replace value
                    SetItem(property.Key, obj);
                }
            }
        }
    }

    public override bool Equals(Node? other)
    {
        if (other is not NodeObject nodeObject || nodeObject == null)
        {
            return false;
        }

        if (nodeObject.Count != Count)
        {
            return false;
        }

        foreach (var item in this)
        {
            var otherItem = nodeObject.GetItem(item.Key);

            if (item.Value == null)
            {
                return otherItem == null;
            }

            if (!item.Value.Equals(otherItem))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        if (_dictionary == null)
        {
            return 0;
        }

        var hashCode = new HashCode();

        foreach (var item in _dictionary)
        {
            hashCode.Add(item.Key);
            hashCode.Add(item.Value);
        }

        return hashCode.ToHashCode();
    }
}
