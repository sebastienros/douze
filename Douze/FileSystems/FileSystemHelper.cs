using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Douze.FileSystems
{
    internal static class FileSystemHelper
    {   
        public static string NormalizePath(string path)
        {
            if (path.Contains("\\"))
            {
                return path.Replace("\\", "/");
            }

            return path;
        }

        public static FPath GetParentDirectory(FPath path)
        {
            if (path == FPath.Root)
            {
                return FPath.Empty;
            }

            var index = path.FullName.LastIndexOf('/');

            if (index == 0 || index == -1)
            {
                return "/";
            }

            return path.FullName.Substring(0, index - 1);
        }
    }
}
