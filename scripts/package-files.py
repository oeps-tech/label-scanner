"""Create an app update ZIP, including Windows paths exceeding MAX_PATH.

No files are deleted; the target must not already exist. ZIP names stay relative
and portable. This runs only at build time using the already provisioned Python.
"""
from pathlib import Path
import os
import sys
import zipfile


def long_path(path: str) -> str:
    full = os.path.abspath(path)
    return "\\\\?\\" + full if os.name == "nt" and not full.startswith("\\\\?\\") else full


def main() -> None:
    root = Path(long_path(sys.argv[1]))
    output = Path(long_path(sys.argv[2]))
    with zipfile.ZipFile(output, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as archive:
        count = 0
        for directory, folders, files in os.walk(root):
            folders[:] = sorted(name for name in folders if name != "__pycache__")
            for name in sorted(files):
                path = Path(directory) / name
                if path.suffix.lower() == ".pdb":
                    continue
                archive.write(path, path.relative_to(root).as_posix())
                count += 1
    print(f"Archived {count} files to {sys.argv[2]}")


if __name__ == "__main__":
    main()
