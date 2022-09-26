using System.Collections;
using System.Diagnostics;

namespace Douze.Nodes;

internal partial class NodeObject : IDictionary<string, Node?>
{
    private Dictionary<string, Node?>? _dictionary;

    /// <summary>
    ///   Adds an element with the provided property name and value to the <see cref="NodeObject"/>.
    /// </summary>
    /// <param name="propertyName">The property name of the element to add.</param>
    /// <param name="value">The value of the element to add.</param>
    /// <exception cref="ArgumentNullException">
    ///   <paramref name="propertyName"/>is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///   An element with the same property name already exists in the <see cref="NodeObject"/>.
    /// </exception>
    public void Add(string propertyName, Node? value)
    {
        InitializeIfRequired();
        Debug.Assert(_dictionary != null);
        _dictionary.Add(propertyName, value);
    }

    /// <summary>
    ///   Adds the specified property to the <see cref="NodeObject"/>.
    /// </summary>
    /// <param name="property">
    ///   The KeyValuePair structure representing the property name and value to add to the <see cref="NodeObject"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    ///   An element with the same property name already exists in the <see cref="NodeObject"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    ///   The property name of <paramref name="property"/> is <see langword="null"/>.
    /// </exception>
    public void Add(KeyValuePair<string, Node?> property)
    {
        Add(property.Key, property.Value);
    }

    /// <summary>
    ///   Removes all elements from the <see cref="NodeObject"/>.
    /// </summary>
    public void Clear()
    {
        if (_dictionary == null)
        {
            return;
        }

        _dictionary.Clear();
    }

    /// <summary>
    ///   Determines whether the <see cref="NodeObject"/> contains an element with the specified property name.
    /// </summary>
    /// <param name="propertyName">The property name to locate in the <see cref="NodeObject"/>.</param>
    /// <returns>
    ///   <see langword="true"/> if the <see cref="NodeObject"/> contains an element with the specified property name; otherwise, <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    ///   <paramref name="propertyName"/> is <see langword="null"/>.
    /// </exception>
    public bool ContainsKey(string propertyName)
    {
        InitializeIfRequired();
        Debug.Assert(_dictionary != null);
        return _dictionary.ContainsKey(propertyName);
    }

    /// <summary>
    ///   Gets the number of elements contained in <see cref="NodeObject"/>.
    /// </summary>
    public int Count
    {
        get
        {
            InitializeIfRequired();
            Debug.Assert(_dictionary != null);
            return _dictionary.Count;
        }
    }

    /// <summary>
    ///   Removes the element with the specified property name from the <see cref="NodeObject"/>.
    /// </summary>
    /// <param name="propertyName">The property name of the element to remove.</param>
    /// <returns>
    ///   <see langword="true"/> if the element is successfully removed; otherwise, <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    ///   <paramref name="propertyName"/> is <see langword="null"/>.
    /// </exception>
    public bool Remove(string propertyName)
    {
        if (propertyName == null)
        {
            throw new ArgumentNullException(nameof(propertyName));
        }

        InitializeIfRequired();
        Debug.Assert(_dictionary != null);

        var success = _dictionary.Remove(propertyName);

        return success;
    }

    /// <summary>
    ///   Determines whether the <see cref="NodeObject"/> contains a specific property name and <see cref="Node"/> reference.
    /// </summary>
    /// <param name="item">The element to locate in the <see cref="NodeObject"/>.</param>
    /// <returns>
    ///   <see langword="true"/> if the <see cref="NodeObject"/> contains an element with the property name; otherwise, <see langword="false"/>.
    /// </returns>
    bool ICollection<KeyValuePair<string, Node?>>.Contains(KeyValuePair<string, Node?> item)
    {
        InitializeIfRequired();
        Debug.Assert(_dictionary != null);
        return _dictionary.Contains(item);
    }

    /// <summary>
    ///   Copies the elements of the <see cref="NodeObject"/> to an array of type KeyValuePair starting at the specified array index.
    /// </summary>
    /// <param name="array">
    ///   The one-dimensional Array that is the destination of the elements copied from <see cref="NodeObject"/>.
    /// </param>
    /// <param name="index">The zero-based index in <paramref name="array"/> at which copying begins.</param>
    /// <exception cref="ArgumentNullException">
    ///   <paramref name="array"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///   <paramref name="index"/> is less than 0.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///   The number of elements in the source ICollection is greater than the available space from <paramref name="index"/>
    ///   to the end of the destination <paramref name="array"/>.
    /// </exception>
    void ICollection<KeyValuePair<string, Node?>>.CopyTo(KeyValuePair<string, Node?>[] array, int index)
    {
        InitializeIfRequired();
        Debug.Assert(_dictionary != null);

        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        foreach (var item in _dictionary)
        {
            if (index >= array.Length)
            {
                throw new ArgumentException("New array too small");
            }

            array[index++] = item;
        }
    }

    /// <summary>
    ///   Returns an enumerator that iterates through the <see cref="NodeObject"/>.
    /// </summary>
    /// <returns>
    ///   An enumerator that iterates through the <see cref="NodeObject"/>.
    /// </returns>
    public IEnumerator<KeyValuePair<string, Node?>> GetEnumerator()
    {
        InitializeIfRequired();
        Debug.Assert(_dictionary != null);
        return _dictionary.GetEnumerator();
    }

    /// <summary>
    ///   Removes a key and value from the <see cref="NodeObject"/>.
    /// </summary>
    /// <param name="item">
    ///   The KeyValuePair structure representing the property name and value to remove from the <see cref="NodeObject"/>.
    /// </param>
    /// <returns>
    ///   <see langword="true"/> if the element is successfully removed; otherwise, <see langword="false"/>.
    /// </returns>
    bool ICollection<KeyValuePair<string, Node?>>.Remove(KeyValuePair<string, Node?> item) => Remove(item.Key);

    /// <summary>
    ///   Gets a collection containing the property names in the <see cref="NodeObject"/>.
    /// </summary>
    ICollection<string> IDictionary<string, Node?>.Keys
    {
        get
        {
            InitializeIfRequired();
            Debug.Assert(_dictionary != null);
            return _dictionary.Keys;
        }
    }

    /// <summary>
    ///   Gets a collection containing the property values in the <see cref="NodeObject"/>.
    /// </summary>
    ICollection<Node?> IDictionary<string, Node?>.Values
    {
        get
        {
            InitializeIfRequired();
            Debug.Assert(_dictionary != null);
            return _dictionary.Values;
        }
    }

    /// <summary>
    ///   Gets the value associated with the specified property name.
    /// </summary>
    /// <param name="propertyName">The property name of the value to get.</param>
    /// <param name="jsonNode">
    ///   When this method returns, contains the value associated with the specified property name, if the property name is found;
    ///   otherwise, <see langword="null"/>.
    /// </param>
    /// <returns>
    ///   <see langword="true"/> if the <see cref="NodeObject"/> contains an element with the specified property name; otherwise, <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    ///   <paramref name="propertyName"/> is <see langword="null"/>.
    /// </exception>
    bool IDictionary<string, Node?>.TryGetValue(string propertyName, out Node? jsonNode)
    {
        InitializeIfRequired();
        Debug.Assert(_dictionary != null);
        return _dictionary.TryGetValue(propertyName, out jsonNode);
    }

    /// <summary>
    ///   Returns <see langword="false"/>.
    /// </summary>
    bool ICollection<KeyValuePair<string, Node?>>.IsReadOnly => false;

    /// <summary>
    ///   Returns an enumerator that iterates through the <see cref="NodeObject"/>.
    /// </summary>
    /// <returns>
    ///   An enumerator that iterates through the <see cref="NodeObject"/>.
    /// </returns>
    IEnumerator IEnumerable.GetEnumerator()
    {
        InitializeIfRequired();
        Debug.Assert(_dictionary != null);
        return _dictionary.GetEnumerator();
    }

    private void InitializeIfRequired()
    {
        if (_dictionary != null)
        {
            return;
        }

        _dictionary = new Dictionary<string, Node?>();
    }
}
