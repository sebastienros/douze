using System.Collections;

namespace Contoso.Widgets
{
    /// <summary>
    /// Widgets and the parts they are made of.
    /// </summary>
    internal static class NamespaceDoc
    {
    }

    /// <summary>
    /// A widget that can be <see cref="Resize(int, int)">resized</see>.
    /// </summary>
    /// <remarks>
    /// <para>The first paragraph.</para>
    /// <para>The second one, with <c>code</c> and <see langword="null"/>.</para>
    /// <list type="bullet">
    ///   <item><term>One</term><description>the first item</description></item>
    ///   <item><description>the second item</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    ///     var widget = new Widget("a");
    ///     widget.Resize(1, 2);
    /// </code>
    /// </example>
    /// <seealso cref="IShape"/>
    /// <seealso href="https://example.com/widgets">Widget guide</seealso>
    public class Widget : WidgetBase, IShape, IEquatable<Widget>, IDisposable
    {
        /// <summary>The name of a widget created without one.</summary>
        public const string DefaultName = "widget";

        /// <summary>The largest size, checked by <see cref="Hidden"/> and <see cref="InternalThing"/>.</summary>
        public static readonly int MaxSize = 100;

        private int _width;

        /// <summary>Creates a widget.</summary>
        /// <param name="name">The name of the widget.</param>
        public Widget(string name = DefaultName)
        {
            Name = name;
        }

        /// <summary>Creates a widget of a size.</summary>
        /// <param name="name">The name.</param>
        /// <param name="size">The size.</param>
        public Widget(string name, Size size) : this(name)
        {
            _width = size.Width;
        }

        /// <summary>Raised when the widget is resized.</summary>
        public event EventHandler<SizeChangedEventArgs>? Resized;

        /// <summary>Gets the name.</summary>
        /// <value>The name, never <see langword="null"/>.</value>
        public string Name { get; }

        /// <summary>Gets or sets a value attached to the widget.</summary>
        public object? Tag { get; set; }

        /// <summary>Gets the label, set when the widget is created.</summary>
        public string Label { get; init; } = "";

        /// <summary>Gets the width.</summary>
        public int Width { get => _width; protected set => _width = value; }

        /// <summary>Gets the part at an index.</summary>
        /// <param name="index">The index of the part.</param>
        public int this[int index] => index;

        /// <summary>Resizes the widget.</summary>
        /// <param name="width">The new width.</param>
        /// <param name="height">The new height.</param>
        /// <returns><see langword="true"/> when the size changed.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is negative.</exception>
        public bool Resize(int width, int height)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(width);
            _width = width;
            Resized?.Invoke(this, new SizeChangedEventArgs());
            return height >= 0;
        }

        /// <summary>Resizes the widget to a <see cref="Size"/>.</summary>
        /// <param name="size">The new size.</param>
        /// <returns><see langword="true"/> when the size changed.</returns>
        public bool Resize(Size size) => Resize(size.Width, size.Height);

        /// <inheritdoc/>
        public override string Describe() => Name;

        /// <inheritdoc/>
        public double Area() => _width;

        /// <inheritdoc cref="Resize(int, int)"/>
        public bool TryResize(int width, int height) => width >= 0 && Resize(width, height);

        /// <summary>Converts the widget.</summary>
        /// <typeparam name="T">The type to convert to.</typeparam>
        /// <param name="converter">Converts.</param>
        /// <returns>The converted widget.</returns>
        public T Convert<T>(Func<Widget, T> converter) where T : class, new() => converter(this);

        /// <summary>Reads values.</summary>
        /// <param name="value">The value read.</param>
        /// <param name="other">A value read and written.</param>
        /// <param name="size">A size read only.</param>
        /// <returns>Whether it worked.</returns>
        public bool TryGet(out int value, ref int other, in Size size)
        {
            value = other + size.Width;
            return true;
        }

        /// <summary>Adds values.</summary>
        /// <param name="values">The values.</param>
        public void Add(params int[] values)
        {
        }

        /// <summary>Paints the widget.</summary>
        /// <param name="color">The color.</param>
        /// <param name="options">The options.</param>
        public void Paint(Color color = Color.Red, Options options = Options.None)
        {
        }

        /// <summary>Grows the widget.</summary>
        [Obsolete("Use Resize instead.")]
        public void Grow()
        {
        }

        /// <summary>Called when the widget was resized.</summary>
        /// <param name="args">What changed.</param>
        protected virtual void OnResized(SizeChangedEventArgs args)
        {
        }

        /// <summary>Not part of the API.</summary>
        internal void Hidden()
        {
        }

        /// <inheritdoc/>
        public bool Equals(Widget? other) => other?.Name == Name;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => Equals(obj as Widget);

        /// <inheritdoc/>
        public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

        /// <inheritdoc/>
        double IShape.Perimeter => _width * 4;

        /// <summary>Disposes the widget.</summary>
        void IDisposable.Dispose()
        {
        }

        /// <summary>Combines two widgets.</summary>
        /// <param name="left">The first widget.</param>
        /// <param name="right">The second widget.</param>
        /// <returns>A widget.</returns>
        public static Widget operator +(Widget left, Widget right) => new(left.Name + right.Name);

        /// <summary>The name of a widget.</summary>
        /// <param name="widget">The widget.</param>
        public static implicit operator string(Widget widget) => widget.Name;

        /// <summary>A part of a widget.</summary>
        public class Part
        {
        }
    }

    /// <summary>The base of the widgets.</summary>
    public abstract class WidgetBase
    {
        /// <summary>Creates a widget.</summary>
        protected WidgetBase()
        {
        }

        /// <summary>Describes the widget.</summary>
        /// <returns>A description of the widget.</returns>
        public abstract string Describe();

        /// <summary>Called when the widget changed.</summary>
        protected virtual void OnChanged()
        {
        }
    }

    /// <summary>A shape.</summary>
    public interface IShape
    {
        /// <summary>Gets the perimeter.</summary>
        double Perimeter { get; }

        /// <summary>Computes the area.</summary>
        /// <returns>The area of the shape.</returns>
        double Area();
    }

    /// <summary>A size.</summary>
    /// <param name="Width">The width.</param>
    /// <param name="Height">The height.</param>
    public readonly record struct Size(int Width, int Height);

    /// <summary>A person.</summary>
    /// <param name="Name">The name.</param>
    public record Person(string Name);

    /// <summary>The arguments of <see cref="Widget.Resized"/>.</summary>
    public sealed class SizeChangedEventArgs : EventArgs
    {
    }

    /// <summary>A widget that cannot be derived from.</summary>
    public sealed class Gadget : WidgetBase
    {
        /// <inheritdoc/>
        public override string Describe() => "gadget";

        /// <summary>Not visible: the class is sealed.</summary>
        protected override void OnChanged()
        {
        }

#pragma warning disable CS0628 // A protected type in a sealed class, which the API does not list.
        /// <summary>Not visible: the class is sealed.</summary>
        protected class Part
        {
        }
#pragma warning restore CS0628
    }

    /// <summary>A color.</summary>
    public enum Color : byte
    {
        /// <summary>Red.</summary>
        Red = 1,

        /// <summary>Green.</summary>
        Green = 2,
    }

    /// <summary>Options.</summary>
    [Flags]
    public enum Options
    {
        /// <summary>None.</summary>
        None = 0,

        /// <summary>Fast.</summary>
        Fast = 1,
    }

    /// <summary>Handles a widget.</summary>
    /// <typeparam name="T">The widget type.</typeparam>
    /// <typeparam name="TResult">The result.</typeparam>
    /// <param name="widget">The widget.</param>
    /// <returns>The result.</returns>
    public delegate TResult WidgetHandler<in T, out TResult>(T widget) where T : Widget;

    /// <summary>Extensions of <see cref="Widget"/>.</summary>
    public static class WidgetExtensions
    {
        /// <summary>Renames a widget.</summary>
        /// <param name="widget">The widget.</param>
        /// <param name="name">The new name.</param>
        /// <returns>A new widget.</returns>
        public static Widget Named(this Widget widget, string name) => new(name) { Tag = widget.Tag };
    }

    /// <summary>A bag of <typeparamref name="T"/>, see <see cref="List{T}"/>.</summary>
    /// <typeparam name="T">The type of the items.</typeparam>
    public class Bag<T> : IEnumerable<T> where T : notnull
    {
        private readonly List<T> _items = [];

        /// <summary>Adds an item.</summary>
        /// <param name="item">The item.</param>
        public void Add(T item) => _items.Add(item);

        /// <summary>Maps the items.</summary>
        /// <typeparam name="TOut">The type of the results.</typeparam>
        /// <param name="map">Maps an item.</param>
        /// <returns>The results.</returns>
        public IEnumerable<TOut> Map<TOut>(Func<T, TOut> map) => _items.Select(map);

        /// <inheritdoc/>
        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        /// <summary>Gets an enumerator of the items, as objects.</summary>
        /// <returns>The enumerator.</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>An entry of the bag.</summary>
        /// <typeparam name="TKey">The key.</typeparam>
        public class Entry<TKey>
        {
            /// <summary>Gets the value.</summary>
            /// <param name="key">The key.</param>
            /// <returns>The value.</returns>
            public T? Get(TKey key) => default;
        }
    }

    /// <summary>Converts values.</summary>
    /// <typeparam name="TIn">The type of the values.</typeparam>
    /// <typeparam name="TOut">The type of the results.</typeparam>
    public interface IConverter<TIn, TOut>
    {
        /// <summary>Converts a value.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The result.</returns>
        TOut Convert(TIn value);
    }

    /// <summary>Converts native integers to text and back.</summary>
    public sealed class NumberConverter : IConverter<nint, string>, IConverter<string, Bag<int>>
    {
        /// <summary>Writes a native integer.</summary>
        /// <param name="value">The integer.</param>
        /// <returns>The text.</returns>
        string IConverter<nint, string>.Convert(nint value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Reads a bag.</summary>
        /// <param name="value">The text.</param>
        /// <returns>The bag.</returns>
        Bag<int> IConverter<string, Bag<int>>.Convert(string value) => [];
    }

    /// <summary>Reads spans.</summary>
    public ref struct SpanReader
    {
        /// <summary>Reads a value.</summary>
        /// <param name="span">The span.</param>
        /// <returns>The first value.</returns>
        public static ref readonly int First(ReadOnlySpan<int> span) => ref span[0];
    }

    internal sealed class InternalThing
    {
    }
}

namespace Contoso.Widgets.Drawing
{
    /// <summary>Draws widgets on a <see cref="Canvas"/>.</summary>
    public static class Canvas
    {
        /// <summary>Draws a widget.</summary>
        /// <param name="widget">The widget.</param>
        /// <param name="color">The color, <see cref="Color.Red"/> by default.</param>
        public static void Draw(Widget widget, Color color = Color.Red)
        {
        }
    }
}
