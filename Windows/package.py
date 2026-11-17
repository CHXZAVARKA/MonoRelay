"""Publish a portable MonoRelay ZIP using the matching official scrcpy archive."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
from shutil import copyfileobj
import subprocess
import tempfile
from zipfile import ZIP_DEFLATED, ZipFile

RUNTIMES = {
    "win-x64": ("scrcpy-win64-v5.0.1.zip", "b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5"),
    "win-arm64": ("scrcpy-winarm64-v5.0.1.zip", "16c3fc2068df64946f670e3504b4752073ebae47a416a45b46e79a5ab19c689b"),
}
WINDOWS = Path(__file__).resolve().parent
ROOT = WINDOWS.parent


def package(runtime: str, engine: Path, destination: Path) -> None:
    archive_name, digest = RUNTIMES[runtime]
    if hashlib.sha256(engine.read_bytes()).hexdigest() != digest:
        raise ValueError(f"Use the official {archive_name} with SHA-256 {digest}")
    if destination.exists():
        raise FileExistsError(destination)

    with tempfile.TemporaryDirectory(prefix="monorelay-") as temporary:
        app = Path(temporary) / "MonoRelay"
        subprocess.run([
            "dotnet", "publish", str(WINDOWS / "MonoRelay.Windows.csproj"), "-c", "Release",
            "-r", runtime, "--self-contained", "true", "-o", str(app),
        ], check=True)
        assets = json.loads((WINDOWS / "obj/project.assets.json").read_text(encoding="utf-8"))
        notices = app / "Licenses"
        notices.mkdir()
        package_ids = {
            f"microsoft.netcore.app.runtime.{runtime}",
            f"microsoft.netcore.app.host.{runtime}",
            f"microsoft.windowsdesktop.app.runtime.{runtime}",
        }
        downloads = {
            package["name"].lower(): package["version"]
            for framework in assets["project"]["frameworks"].values()
            for package in framework.get("downloadDependencies", [])
        }
        for identity in sorted(package_ids):
            version_range = downloads[identity].strip("[]")
            version = version_range.split(",")[0].strip()
            for folder in assets["packageFolders"]:
                metadata = Path(folder) / identity / version
                if not metadata.is_dir():
                    continue
                for item in metadata.iterdir():
                    if item.is_file() and ("LICENSE" in item.name.upper() or "NOTICE" in item.name.upper()):
                        (notices / f"{identity}-{item.name}.txt").write_bytes(item.read_bytes())
                break
        if not any("NOTICES" in path.name.upper() for path in notices.iterdir()):
            raise ValueError("The restored .NET runtime packages did not provide their notices.")
        tools = app / "Tools"
        tools.mkdir()
        with ZipFile(engine) as official:
            for entry in official.infolist():
                item = PurePosixPath(entry.filename)
                if entry.is_dir():
                    continue
                if len(item.parts) != 2 or item.parts[0] != archive_name.removesuffix(".zip"):
                    raise ValueError(f"Unexpected ZIP entry {entry.filename!r}")
                with official.open(entry) as source, (tools / item.name).open("wb") as out:
                    copyfileobj(source, out)
        for required in ["adb.exe", "scrcpy.exe", "scrcpy-server", "LICENSE.txt"]:
            if not (tools / required).is_file():
                raise ValueError(f"Runtime is missing {required}")

        (app / "LICENSE-MonoRelay.txt").write_bytes((ROOT / "LICENSE").read_bytes())
        (app / "Сначала прочитай.txt").write_text(
            "Распакуй архив целиком и запусти MonoRelay.exe.\n"
            "ПК и Quest должны быть в одной локальной сети.\n"
            "На Quest нужна включённая беспроводная отладка ADB.\n"
            "При первом подключении введи IP очков и подтверди запрос отладки в шлеме.\n",
            encoding="utf-8-sig",
        )
        (app / "THIRD-PARTY.txt").write_text(
            "MonoRelay MIT notice: LICENSE-MonoRelay.txt.\n"
            "Genymobile scrcpy and its companion tools are from the unmodified official Windows archive.\n"
            f"Source and binary archive: https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1\n"
            f"Archive: {archive_name}\nSHA-256: {digest}\n"
            "Official scrcpy license: Tools/LICENSE.txt.\n"
            "Build definitions and third-party dependencies: https://github.com/Genymobile/scrcpy/tree/v5.0.1/app/deps\n"
            "Microsoft .NET licenses and notices: Licenses/.\n"
            "Sources and licenses: https://github.com/dotnet/runtime and https://github.com/dotnet/winforms\n",
            encoding="utf-8",
        )
        destination.parent.mkdir(parents=True, exist_ok=True)
        with ZipFile(destination, "x", ZIP_DEFLATED, compresslevel=4) as release:
            for file in sorted(app.rglob("*")):
                if file.is_file():
                    release.write(file, file.relative_to(app.parent))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runtime", choices=RUNTIMES)
    parser.add_argument("scrcpy_zip", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    package(args.runtime, args.scrcpy_zip, args.destination)
