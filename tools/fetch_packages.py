#!/usr/bin/env python3
"""
Downloads LibreMetaverse and all its runtime dependencies from NuGet,
extracts netstandard2.0 DLLs (Unity-compatible), and copies them into
Assets/Plugins/LibreMetaverse/.

Also downloads SkiaSharp native android-arm64 .so for CoreJ2K texture decoding.

Usage:
    python3 tools/fetch_packages.py
"""

import os, sys, json, zipfile, shutil, hashlib, subprocess
from pathlib import Path
from urllib.request import urlretrieve, urlopen
from urllib.error import URLError

ROOT         = Path(__file__).parent.parent
PLUGINS_DIR  = ROOT / "Assets" / "Plugins" / "LibreMetaverse"
CACHE_DIR    = ROOT / ".nuget-cache"
NUGET_BASE   = "https://api.nuget.org/v3-flatcontainer"

PLUGINS_DIR.mkdir(parents=True, exist_ok=True)
CACHE_DIR.mkdir(parents=True, exist_ok=True)

# TFM preference order (Unity supports netstandard2.0 and net6.0+)
TFM_PREFERENCE = [
    "netstandard2.0",
    "netstandard2.1",
    "net6.0",
    "net8.0",
    "netstandard1.6",
    "netstandard1.5",
    "netstandard1.4",
    "netstandard1.3",
    "netstandard1.2",
    "netstandard1.1",
    "netstandard1.0",
]

# Packages Unity already ships — do NOT include, they will conflict
UNITY_BUNDLED = {
    "system.runtime",
    "system.collections",
    "system.linq",
    "system.io",
    "system.threading",
    "system.reflection",
    "system.text.encoding",
    "system.text.regularexpressions",
    "system.globalization",
    "system.net.http",
    "system.net.primitives",
    "system.net.sockets",
    "system.security.cryptography.x509certificates",
    "system.security.cryptography.algorithms",
    "system.security.cryptography.primitives",
    "system.security.principal",
    "system.xml",
    "system.xml.linq",
    "system.xml.xdocument",
    "system.componentmodel",
    "system.diagnostics.debug",
    "system.diagnostics.tracing",
    "netstandard",
    "microsoft.netcore.platforms",
    "microsoft.netcore.targets",
    "microsoft.win32.primitives",
    "microsoft.win32.registry",
    # Unity ships its own logging
    "microsoft.extensions.logging",
    "microsoft.extensions.logging.abstractions",
    "microsoft.extensions.logging.console",
    "microsoft.extensions.options",
    "microsoft.extensions.primitives",
    "microsoft.extensions.dependencyinjection",
    "microsoft.extensions.dependencyinjection.abstractions",
    "microsoft.extensions.objectpool",
}

# Root packages to install
ROOT_PACKAGES = [
    ("libremetaverse",                  "2.6.7"),
    ("libremetaverse.types",            "2.6.7"),
    ("libremetaverse.structureddata",   "2.6.7"),
]

resolved  = {}   # id_lower → version
installed = set()


def nuget_url(pkg_id: str, version: str) -> str:
    return f"{NUGET_BASE}/{pkg_id.lower()}/{version}/{pkg_id.lower()}.{version}.nupkg"


def latest_version(pkg_id: str) -> str | None:
    try:
        url  = f"{NUGET_BASE}/{pkg_id.lower()}/index.json"
        data = json.loads(urlopen(url, timeout=10).read())
        return data["versions"][-1]
    except Exception:
        return None


def get_nuspec_deps(zf: zipfile.ZipFile, pkg_id: str, version: str):
    """Return list of (dep_id, dep_version) from the nuspec, preferring netstandard2.0."""
    nuspec_names = [n for n in zf.namelist() if n.endswith(".nuspec")]
    if not nuspec_names:
        return []

    import xml.etree.ElementTree as ET
    root = ET.fromstring(zf.read(nuspec_names[0]))
    ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
    if not ns["n"]:
        ns["n"] = "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd"

    deps = []
    # Try to find the netstandard2.0 dependency group
    for group in root.findall(".//n:dependencies/n:group", ns):
        tfm = group.get("targetFramework", "").lower()
        if "netstandard2" in tfm or tfm == "":
            for dep in group.findall("n:dependency", ns):
                dep_id  = dep.get("id", "")
                dep_ver = dep.get("version", "").strip("[]() ")
                if dep_ver.startswith("["):
                    dep_ver = dep_ver[1:dep_ver.index("]")]
                # Strip trailing .* wildcards
                dep_ver = dep_ver.split(",")[0].strip()
                deps.append((dep_id, dep_ver))
            if tfm:   # prefer explicit netstandard group over empty group
                break

    return deps


def best_dll_path(zf: zipfile.ZipFile, pkg_id: str) -> list[str]:
    """Return DLL paths within the zip using our TFM preference."""
    all_dlls = [n for n in zf.namelist()
                if n.startswith("lib/") and n.endswith(".dll") and "/_" not in n]

    for tfm in TFM_PREFERENCE:
        matches = [d for d in all_dlls if f"/lib/{tfm}/" in d or d.startswith(f"lib/{tfm}/")]
        if matches:
            return matches

    # Fallback: any dll in lib/
    return all_dlls


def download_package(pkg_id: str, version: str):
    """Download .nupkg, cache it, return Path to the zip."""
    cache_path = CACHE_DIR / f"{pkg_id.lower()}.{version}.nupkg"
    if cache_path.exists():
        return cache_path

    url = nuget_url(pkg_id, version)
    print(f"  ↓  {pkg_id} {version}")
    try:
        urlretrieve(url, cache_path)
    except Exception as e:
        print(f"  ✗  Failed to download {pkg_id}: {e}")
        return None
    return cache_path


def install(pkg_id: str, version: str, depth: int = 0):
    key = pkg_id.lower()
    if key in installed or key in UNITY_BUNDLED:
        return
    installed.add(key)

    indent = "  " * depth
    pkg_path = download_package(pkg_id, version)
    if pkg_path is None:
        return

    with zipfile.ZipFile(pkg_path) as zf:
        dll_paths = best_dll_path(zf, pkg_id)

        for dll_path in dll_paths:
            dll_name = Path(dll_path).name
            dest     = PLUGINS_DIR / dll_name

            if dest.exists():
                # Skip if same content
                existing = hashlib.md5(dest.read_bytes()).hexdigest()
                incoming = hashlib.md5(zf.read(dll_path)).hexdigest()
                if existing == incoming:
                    continue

            dest.write_bytes(zf.read(dll_path))
            print(f"{indent}  ✓  {dll_name}  [{Path(dll_path).parent.name}]")

        # Recurse deps
        for dep_id, dep_ver in get_nuspec_deps(zf, pkg_id, version):
            if dep_id.lower() not in UNITY_BUNDLED and dep_id.lower() not in installed:
                install(dep_id, dep_ver, depth + 1)


def install_skia_native():
    """
    SkiaSharp requires native .so on Android.
    Download the android-arm64 runtimes for the version CoreJ2K.Skia uses.
    """
    # Find which SkiaSharp version CoreJ2K.Skia needs
    corej2k_path = CACHE_DIR / "corej2k.skia.*.nupkg"
    import glob
    matches = glob.glob(str(CACHE_DIR / "corej2k.skia.*.nupkg"))
    if not matches:
        return

    skia_ver = None
    with zipfile.ZipFile(matches[0]) as zf:
        for dep_id, dep_ver in get_nuspec_deps(zf, "corej2k.skia", ""):
            if dep_id.lower() == "skiasharp":
                skia_ver = dep_ver
                break

    if not skia_ver:
        skia_ver = "2.88.8"   # known good version

    # Download SkiaSharp.NativeAssets.Android
    native_path = download_package("SkiaSharp.NativeAssets.Android", skia_ver)
    if native_path is None:
        return

    dest_jni = ROOT / "Assets" / "Plugins" / "Android" / "libs" / "arm64-v8a"
    dest_jni.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(native_path) as zf:
        for name in zf.namelist():
            if "arm64" in name and name.endswith(".so"):
                so_name = Path(name).name
                dest = dest_jni / so_name
                dest.write_bytes(zf.read(name))
                print(f"  ✓  {so_name}  [native/android-arm64]")


def write_plugin_meta(dll_path: Path):
    """Write a Unity .meta file that enables the DLL for Android (ARM64) standalone."""
    meta = dll_path.with_suffix(".dll.meta")
    if meta.exists():
        return
    guid = hashlib.md5(dll_path.name.encode()).hexdigest()
    meta.write_text(f"""fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      Android: Android
    second:
      enabled: 1
      settings:
        CPU: ARM64
  userData:
  assetBundleName:
  assetBundleVariant:
""")


def main():
    print("=== SLQuest NuGet package installer ===\n")
    print(f"Output: {PLUGINS_DIR}\n")

    for pkg_id, version in ROOT_PACKAGES:
        print(f"Installing {pkg_id} {version} and dependencies...")
        install(pkg_id, version)

    print("\nInstalling SkiaSharp native libraries for Android...")
    install_skia_native()

    print("\nWriting Unity .meta files...")
    for dll in PLUGINS_DIR.glob("*.dll"):
        write_plugin_meta(dll)

    count = len(list(PLUGINS_DIR.glob("*.dll")))
    print(f"\n✅ Done — {count} DLLs in {PLUGINS_DIR.relative_to(ROOT)}")
    print("\nNote: Open the project in Unity and check for any package conflicts.")
    print("If you see 'duplicate assembly' errors, delete the conflicting DLL from Plugins/LibreMetaverse/.")


if __name__ == "__main__":
    main()
