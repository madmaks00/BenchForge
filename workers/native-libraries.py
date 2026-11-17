#!/usr/bin/env python3
"""Fetch pinned official native libraries before benchmarks, verify SHA-256.

Downloaded Oracle binaries keep their vendor license; they are never committed.
No machine-wide library paths or database services are installed.
"""

import hashlib
from pathlib import Path
import platform
import shutil
import stat
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

DUCK = {
    "windows-x64": (
        "windows-amd64",
        "44cf59583f9951d2cb09b1bf115a63ecb2d8901e363903029d86c7d8683fe96a",
    ),
    "windows-arm64": (
        "windows-arm64",
        "c63285647e94c46d309b60d5dd1ff3cb2cdf9ca03e415a818ff7d8dbf838e0e8",
    ),
    "linux-x64": (
        "linux-amd64",
        "b845005f5132a7d8180057c35e14a7626632258782f871a90861b19c1c03841b",
    ),
    "linux-arm64": (
        "linux-arm64",
        "b72ed9f05003f5e9d2015f7ceada6416b377d9dd33169cdde3c9e33897856eee",
    ),
    "osx-x64": (
        "osx-universal",
        "e0bc007d9b0094c0970ac1847a8601d10aad07cbd2910ce586ec810f77b638d6",
    ),
    "osx-arm64": (
        "osx-universal",
        "e0bc007d9b0094c0970ac1847a8601d10aad07cbd2910ce586ec810f77b638d6",
    ),
}
ORACLE = {
    "windows-x64": (
        "nt",
        "2326400/instantclient-basiclite-windows.x64-23.26.4.0.0.zip",
        "7586c02a2926f619a0b16a9ae32e85bb3605798aa177155b065fffb48a1ee0b8",
    ),
    "linux-x64": (
        "linux",
        "2326400/instantclient-basiclite-linux.x64-23.26.4.0.0.zip",
        "d049e593bba0041feb15c14ea894e2f1a437377462b99d9bdb6b7ab30b2dd660",
    ),
    "linux-arm64": (
        "linux",
        "2326400/instantclient-basiclite-linux.arm64-23.26.4.0.0.zip",
        "61d55b028e9ba5792fbf497e9bcb8f2f14116ed81fc7a8625afc3dd5d0f28c27",
    ),
    "osx-arm64": (
        "mac",
        "2326300/instantclient-basiclite-macos.arm64-23.26.3.0.0.dmg",
        "7e3d1faeb2b3490556f21ce5f28ddb7302616cef3dd2356c9b31bd26e70ae1c7",
    ),
}


def fetch(url, expected, cache):
    path = cache / Path(url).name
    if (
        path.is_file()
        and hashlib.file_digest(path.open("rb"), "sha256").hexdigest() == expected
    ):
        return path
    temporary = path.with_suffix(path.suffix + ".part")
    print("Downloading " + path.name, flush=True)
    with (
        urllib.request.urlopen(url, timeout=120) as response,
        temporary.open("wb") as output,
    ):
        shutil.copyfileobj(response, output)
    with temporary.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != expected:
        temporary.unlink()
        raise ValueError("Native library checksum mismatch: " + path.name)
    temporary.replace(path)
    return path


def copy_zip(archive, target, duck=False):
    # Flat extraction avoids archive traversal and preserves license notices.
    with zipfile.ZipFile(archive) as bundle:
        for item in bundle.infolist():
            name = Path(item.filename).name
            if not name or item.is_dir():
                continue
            if duck and name not in (
                "duckdb.dll",
                "libduckdb.so",
                "libduckdb.dylib",
                "LICENSE",
            ):
                continue
            if stat.S_ISLNK(item.external_attr >> 16):
                link = Path(bundle.read(item).decode("utf-8"))
                if link.is_absolute() or len(link.parts) != 1 or link.name in ("", ".", ".."):
                    raise ValueError("Native library link escapes its bundle")
                destination = target / name
                destination.unlink(missing_ok=True)
                destination.symlink_to(link.name)
                continue
            with bundle.open(item) as source, (target / name).open("wb") as output:
                shutil.copyfileobj(source, output)


def prepare(engine, target, tools):
    system = {"Windows": "windows", "Linux": "linux", "Darwin": "osx"}[
        platform.system()
    ]
    arch = "arm64" if platform.machine().lower() in ("aarch64", "arm64") else "x64"
    key = system + "-" + arch
    native = target.resolve() / "native"
    native.mkdir(parents=True, exist_ok=True)
    cache = tools.resolve() / "native-cache"
    cache.mkdir(parents=True, exist_ok=True)
    asset, sha = DUCK[key]
    copy_zip(
        fetch(
            "https://github.com/duckdb/duckdb/releases/download/v1.5.6/libduckdb-"
            + asset
            + ".zip",
            sha,
            cache,
        ),
        native,
        duck=True,
    )
    if engine != "rust":
        return
    if key not in ORACLE:
        # A user-installed Instant Client remains usable on these platforms.
        print(
            "Oracle for Rust requires a separately installed Oracle Instant Client on "
            + key,
            flush=True,
        )
        return
    os_path, asset, sha = ORACLE[key]
    archive = fetch(
        "https://download.oracle.com/otn_software/"
        + os_path
        + "/instantclient/"
        + asset,
        sha,
        cache,
    )
    if archive.suffix == ".dmg":
        with tempfile.TemporaryDirectory(prefix="benchforge-oracle-") as temporary:
            mount = Path(temporary) / "mount"
            subprocess.run(
                [
                    "hdiutil",
                    "attach",
                    "-readonly",
                    "-nobrowse",
                    "-mountpoint",
                    str(mount),
                    str(archive),
                ],
                check=True,
            )
            try:
                for path in mount.rglob("*"):
                    if path.is_file() and (
                        ".dylib" in path.name or "LICENSE" in path.name
                    ):
                        shutil.copy2(path, native / path.name)
            finally:
                subprocess.run(["hdiutil", "detach", str(mount)], check=True)
    else:
        copy_zip(archive, native)
    if system == "linux":
        # Ubuntu 24.04 renamed libaio; keep compatibility local to this bundle.
        for directory in (
            Path("/usr/lib/x86_64-linux-gnu"),
            Path("/usr/lib/aarch64-linux-gnu"),
        ):
            source = directory / "libaio.so.1t64"
            destination = native / "libaio.so.1"
            if source.is_file() and not destination.exists():
                destination.symlink_to(source)


if __name__ == "__main__":
    prepare(sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3]))
