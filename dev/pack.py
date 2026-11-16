"""Builds Knightfall.dll in Release and packs the release zip.

python dev/pack.py          makes Knightfall.zip in the mod folder
python dev/pack.py try      also puts the DLL in the mod folder, so the next game start runs it
python dev/pack.py source   takes the DLL out again, so the next game start compiles scripts/
"""

import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

MOD = Path(__file__).resolve().parent.parent
BUILT = ["Knightfall.dll"]
KEPT = ["mod.toml"]


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "zip"
    if mode not in ("zip", "try", "source"):
        sys.exit(__doc__)

    if mode == "source":
        for name in BUILT:
            (MOD / name).unlink(missing_ok=True)
        print("The DLL is out. The game compiles scripts/ on its next start.")
        return

    with tempfile.TemporaryDirectory() as out:
        out = Path(out)
        build = subprocess.run(
            ["dotnet", "build", str(MOD / "dev" / "Knightfall.csproj"), "-c", "Release",
             "-o", str(out), "-nologo", "-v", "q"],
        )
        if build.returncode != 0:
            sys.exit("The build failed, so nothing was packed.")

        zip_path = MOD / "Knightfall.zip"
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as pack:
            for name in KEPT:
                pack.write(MOD / name, f"Knightfall/{name}")
            for name in BUILT:
                pack.write(out / name, f"Knightfall/{name}")
        print(f"Packed {zip_path.name}.")

        if mode == "try":
            for name in BUILT:
                shutil.copy2(out / name, MOD / name)
            print("The DLL is in the mod folder. The game runs it from its next start.")


if __name__ == "__main__":
    main()
