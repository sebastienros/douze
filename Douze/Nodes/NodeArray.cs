namespace Douze.Nodes;

internal partial class NodeArray : Node
{
    private List<Node?>? _list;

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeArray"/> class that is empty.
    /// </summary>
    public NodeArray()
    {
    }

    /// <summary>
    ///   Initializes a new instance of the <see cref="NodeArray"/> class that contains items from the specified array.
    /// </summary>
    /// <param name="items">The items to add to the new <see cref="NodeArray"/>.</param>
    public NodeArray(params Node?[] items) : base()
    {
        InitializeFromArray(items);
    }

    private void InitializeFromArray(Node?[] items)
    {
        var list = new List<Node?>(items);

        _list = list;
    }

    /// <summary>
    ///   Adds an object to the end of the <see cref="NodeArray"/>.
    /// </summary>
    /// <typeparam name="T">The type of object to be added.</typeparam>
    /// <param name="value">
    ///   The object to be added to the end of the <see cref="NodeArray"/>.
    /// </param>
    public void Add<T>(T? value)
    {
        if (value == null)
        {
            Add(null);
        }
        else
        {
            if (value is not Node jNode)
            {
                jNode = new NodeValue<T>(value);
            }

            // Call the IList.Add() implementation.
            Add(jNode);
        }
    }

    internal List<Node?> List
    {
        get
        {
            _list ??= new List<Node?>();
            return _list;
        }
    }

    internal Node? GetItem(int index)
    {
        if (_list == null)
        {
            return null;
        }

        return _list[index];
    }

    internal void SetItem(int index, Node? value)
    {
        List[index] = value;
    }

    public override T GetValue<T>()
    {
        throw new NotImplementedException();
    }

    public override Node Clone()
    {
        var array = new NodeArray();

        if (_list == null)
        {
            return array;
        }

        foreach (var item in _list)
        {
            array.Add(item?.Clone());
        }

        return array;
    }

    public void Merge(NodeArray node, MergeSettings? options = null)
    {
        options ??= MergeSettings.Default;

        switch (options.MergeArrayHandling)
        {
            case MergeArrayHandling.Merge:
                var replaceIndex = Math.Min(List.Count, node.List.Count);

                for (var i = 0; i < replaceIndex; i++)
                {
                    SetItem(i, node.List[i]);
                }

                for (var i = replaceIndex; i < node.List.Count; i++)
                {
                    Add(node.List[i]);
                }

                break;

            case MergeArrayHandling.Concat:

                for (var i = 0; i < node.List.Count; i++)
                {
                    Add(node.List[i]);
                }

                break;

            case MergeArrayHandling.Replace:

                _list = null;

                for (var i = 0; i < node.List.Count; i++)
                {
                    Add(node.List[i]);
                }

                break;

            case MergeArrayHandling.Union:

                var items = new HashSet<Node?>(List);

                foreach (var n in node)
                {
                    if (items.Add(n))
                    {
                        Add(n);
                    }
                }

                break;
        }
    }

    public override bool Equals(Node? other)
    {
        if (other is not NodeArray nodeArray || nodeArray == null)
        {
            return false;
        }

        if (nodeArray.List.Count != List.Count)
        {
            return false;
        }

        return nodeArray.List.SequenceEqual(List);
    }

    public override int GetHashCode()
    {
        if (_list == null)
        {
            return 0;
        }

        var hashCode = new HashCode();

        foreach (var node in _list)
        {
            hashCode.Add(node);
        }

        return hashCode.ToHashCode();
    }
}
