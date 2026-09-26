"""Copy embedded Python and only the three locked decoder distributions, including licenses."""
from importlib.metadata import distribution
from pathlib import Path
import shutil
import sys

repo = Path(__file__).resolve().parent.parent
source = Path(sys.executable).resolve().parent
output = Path(sys.argv[1]).resolve()
runtime = output / 'python'
runtime.mkdir(parents=True, exist_ok=False)
for item in source.iterdir():
    if item.is_file() and item.suffix.lower() in {'.exe', '.dll', '.pyd', '.zip', '.txt'}:
        shutil.copy2(item, runtime / item.name)
for line in (repo / 'recognition/requirements.lock').read_text().splitlines():
    line = line.strip()
    if not line or line.startswith('#'):
        continue
    name, expected = line.split('==')
    package = distribution(name)
    if package.version != expected:
        raise ValueError(f'{name}: expected {expected}, installed {package.version}')
    for entry in package.files or []:
        path = Path(package.locate_file(entry)).resolve()
        if path.suffix == '.pyc' or '__pycache__' in path.parts:
            continue
        relative = path.relative_to(source)
        destination = runtime / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, destination)
    print(f'Bundled {name}=={package.version}')
(runtime / 'python312._pth').write_text('python312.zip\n.\n..\\\nimport site\n', encoding='ascii')
code = output / 'recognition'
code.mkdir()
for name in ('__init__.py', 'ipc.py', 'worker.py'):
    shutil.copy2(repo / 'recognition' / name, code / name)
