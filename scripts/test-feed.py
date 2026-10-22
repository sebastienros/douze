"""Serves a folder of .nupkg files as a NuGet v3 feed (service index and flat container), the parts the install
scripts read, so that CI tests them against the packages it just built.

    python3 scripts/test-feed.py <folder of .nupkg files> [port]

The service index is http://127.0.0.1:<port>/index.json (port 5555 by default).
"""

import http.server
import json
import os
import re
import shutil
import sys
import tempfile
import zipfile


def main():
    packages = sys.argv[1]
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 5555
    root = tempfile.mkdtemp(prefix="douze-feed-")
    flat = os.path.join(root, "flat")
    versions = {}

    for name in sorted(os.listdir(packages)):
        if not name.endswith(".nupkg"):
            continue
        with zipfile.ZipFile(os.path.join(packages, name)) as package:
            nuspec = next(entry for entry in package.namelist() if entry.endswith(".nuspec") and "/" not in entry)
            text = package.read(nuspec).decode("utf-8-sig")
        package_id = re.search(r"<id>([^<]+)</id>", text).group(1).lower()
        version = re.search(r"<version>([^<]+)</version>", text).group(1).lower()
        folder = os.path.join(flat, package_id, version)
        os.makedirs(folder, exist_ok=True)
        shutil.copyfile(os.path.join(packages, name), os.path.join(folder, f"{package_id}.{version}.nupkg"))
        versions.setdefault(package_id, []).append(version)

    for package_id, list_ in versions.items():
        with open(os.path.join(flat, package_id, "index.json"), "w") as index:
            json.dump({"versions": list_}, index)

    base = f"http://127.0.0.1:{port}"
    with open(os.path.join(root, "index.json"), "w") as index:
        json.dump({"version": "3.0.0", "resources": [{"@id": f"{base}/flat/", "@type": "PackageBaseAddress/3.0.0"}]}, index)

    print(f"{base}/index.json: {', '.join(f'{p} {v}' for p, vs in versions.items() for v in vs)}", flush=True)
    http.server.ThreadingHTTPServer(("127.0.0.1", port), lambda *args: QuietHandler(*args, directory=root)).serve_forever()


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, format, *args):
        pass


if __name__ == "__main__":
    main()
