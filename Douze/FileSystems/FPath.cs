namespace Douze.FileSystems
{
    /// <summary>
    /// Represent an absolute or relative path.
    /// File paths never end with a directory separator.
    /// Directory paths always end with a directory separator.
    /// </summary>
    public readonly struct FPath : IEquatable<FPath>, IComparable<FPath>
    {
        public static readonly FPath Empty = new FPath(string.Empty, true);

        public static readonly FPath Root = new FPath("/", true);

        public const char DirectorySeparator = '/';

        /// <summary>
        /// Initializes a new instance of the <see cref="UPath"/> struct.
        /// </summary>
        /// <param name="path">The path that will be normalized.</param>
        public FPath(string path) : this(path, true)
        {
        }

        internal FPath(string path, bool validate)
        {
            if (!validate)
            {
                FullName = path;
            }
            else
            {
                FullName = Normalize(path);
            }
        }

        public string FullName { get; }

        public bool IsAbsolute => !IsEmpty && FullName.StartsWith(DirectorySeparator);
        public bool IsRelative => !IsEmpty && !FullName.StartsWith(DirectorySeparator);
        public bool IsDirectory => !IsEmpty && FullName.EndsWith(DirectorySeparator);
        public bool IsFile => !IsEmpty && !FullName.EndsWith(DirectorySeparator);
        public bool IsEmpty => this == Empty;

        public static implicit operator FPath(string path)
        {
            return new FPath(path, true);
        }

        public static FPath Combine(FPath path1, FPath path2)
        {
            ArgumentNullException.ThrowIfNull(nameof(path1));

            ArgumentNullException.ThrowIfNull(nameof(path2));

            if (path1.IsEmpty && path2.IsEmpty)
            {
                return Empty;
            }

            if (path2.IsAbsolute)
            {
                return path2;
            }

            if (path1.IsEmpty)
            {
                return new FPath("/" + path2.FullName, false);
            }
            else
            {
                if (path2.IsEmpty)
                {
                    if (path1.IsAbsolute)
                    {
                        return path1;
                    }
                    else
                    {
                        return new FPath("/" + path1.FullName, false);
                    }
                }
                else
                {
                    if (path1.IsAbsolute)
                    {
                        if (path1 == Root)
                        {
                            return new FPath("/" + path2.FullName, false);
                        }

                        if (path1.IsDirectory)
                        {
                            return new FPath(path1.FullName + path2.FullName, false);
                        }
                        else
                        {
                            return new FPath(path1.FullName + FPath.DirectorySeparator + path2.FullName, false);
                        }
                    }
                    else
                    {
                        if (path1.IsDirectory)
                        {
                            return new FPath("/" + path1.FullName + path2.FullName, false);
                        }
                        else
                        {
                            return new FPath("/" + path1.FullName + FPath.DirectorySeparator + path2.FullName, false);
                        }
                    }
                }
            }
        }

        public static FPath Combine(FPath path1, FPath path2, FPath path3)
        {
            return Combine(FPath.Combine(path1, path2), path3);
        }

        public static FPath Combine(FPath path1, FPath path2, FPath path3, FPath path4)
        {
            return Combine(Combine(path1, path2), Combine(path3, path4));
        }

        public static FPath Combine(params FPath[] paths)
        {
            var path = paths[0];
            var length = paths.Length;

            for (var i = 1; i < length; i++)
            {
                path = Combine(path, paths[i]);
            }

            return path;
        }

        /// <inheritdoc />
        public bool Equals(FPath other)
        {
            return string.Equals(FullName, other.FullName);
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
        {
            if (obj == null)
            {
                return false;
            }

            return obj is FPath path && Equals(path);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return FullName.GetHashCode();
        }

        public static bool operator ==(FPath left, FPath right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(FPath left, FPath right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return FullName;
        }

        private static string Normalize(string path)
        {
            ArgumentNullException.ThrowIfNull(nameof(path));

            if (path == "/")
            {
                return "/";
            }

            if (path == "\\")
            {
                return "/";
            }

            if (path.Contains('\\'))
            {
                return String.Create(path.Length, path, (c, path) =>
                {
                    var length = c.Length;

                    for (var i = 0; i < length; i++)
                    {
                        var n = path[i];
                        c[i] = n == '\\' ? '/' : n;
                    }
                });
            }

            while (path.Contains("//"))
            {
                path = path.Replace("//", "/");
            }

            if (path.Contains(".."))
            {
                ThrowInvalidaPath();
            }

            return path;

            static void ThrowInvalidaPath()
            {
                throw new ArgumentException("Paths can't contain '..'");
            }
        }

        public int CompareTo(FPath other)
        {
            return string.Compare(FullName, other.FullName, StringComparison.Ordinal);
        }

        public FPath GetParentDirectory()
        {
            if (IsEmpty)
            {
                return this;
            }

            if (IsFile)
            {
                return GetDirectory();
            }

            if (this == FPath.Root)
            {
                return FPath.Empty;
            }

            var index = FullName.AsSpan(0, FullName.Length - 1).TrimEnd().LastIndexOf('/');

            if (index == 0 || index == -1)
            {
                return FPath.Root;
            }

            return new FPath(FullName.Substring(0, index) + DirectorySeparator, false);
        }


        /// <summary>
        /// Returns the directory on the current path. It will end with a directory separator.
        /// </summary>
        /// <returns></returns>
        public FPath GetDirectory()
        {
            if (FullName.EndsWith(DirectorySeparator))
            {
                return this;
            }

            var index = FullName.LastIndexOf('/');

            if (index == 0 || index == -1)
            {
                return FPath.Root;
            }

            return new FPath(FullName.Substring(0, index) + DirectorySeparator, false);
        }

        /// <summary>
        /// Adds a directory separator at the beginning of the path.
        /// </summary>
        /// <returns></returns>
        public FPath ToAbsolute()
        {
            if (IsAbsolute || IsEmpty)
            {
                return this;
            }

            return new FPath(DirectorySeparator + FullName, false);
        }

        /// <summary>
        /// Adds a directory separator at the beginning of the path.
        /// </summary>
        /// <returns></returns>
        public FPath ToRelative()
        {
            if (IsRelative || IsEmpty)
            {
                return this;
            }

            return new FPath(FullName[1..], false);
        }

        public bool IsInDirectory(FPath path)
        {
            return FullName.StartsWith(path.GetDirectory().FullName);
        }

        public string GetExtensionWithDot()
        {
            return Path.GetExtension(FullName);
        }
    }
}
