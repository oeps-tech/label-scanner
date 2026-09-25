"""Read the MSI database without installing or changing machine state."""
import json
from pathlib import Path
import sys
import _msi


def rows(database, sql, columns):
    view = database.OpenView(sql)
    view.Execute(None)
    result = []
    while True:
        record = view.Fetch()
        if record is None:
            break
        result.append([record.GetString(i) for i in range(1, columns + 1)])
    view.Close()
    return result


def main():
    path = Path(sys.argv[1]).resolve()
    application_zip = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else None
    version = sys.argv[3] if len(sys.argv) > 3 else "0.1.0"
    database = _msi.OpenDatabase(str(path), _msi.MSIDBOPEN_READONLY)
    properties = dict(rows(database, "SELECT `Property`, `Value` FROM `Property`", 2))
    shortcuts = rows(database, "SELECT `Name`, `Target`, `Arguments` FROM `Shortcut`", 3)
    files = rows(database, "SELECT `FileName` FROM `File`", 1)
    assert properties["ProductName"] == "OEPS Scanner"
    assert properties["ProductVersion"] == version
    assert properties.get("ALLUSERS", "") in ("", "2") and properties.get("MSIINSTALLPERUSER", "1") == "1"
    assert len(shortcuts) == 4 and sum("--app TestClient" in row[2] for row in shortcuts) == 2
    for suffix in ("Scanner.Launcher.exe", f"OEPS.Scanner-{version}-win-x64.zip", "coreclr.dll", "System.Private.CoreLib.dll", "PresentationFramework.dll", "Microsoft.AspNetCore.Hosting.dll", "Microsoft.AspNetCore.Server.Kestrel.Core.dll"):
        assert any(row[0].endswith(suffix) for row in files), f"MSI missing {suffix}"
    if application_zip:
        view = database.OpenView("SELECT `FileName`, `FileSize` FROM `File`")
        view.Execute(None)
        matches = []
        try:
            while (record := view.Fetch()) is not None:
                if record.GetString(1).endswith(application_zip.name):
                    matches.append(record.GetInteger(2))
        finally:
            view.Close()
        assert matches == [application_zip.stat().st_size], "MSI embeds a different application ZIP"
    print(json.dumps({"result": "PASS", "installer": str(path), "product_version": properties["ProductVersion"],
                      "per_user": True, "shortcuts": shortcuts, "file_count": len(files),
                      "application_zip_size_matches": True if application_zip else None,
                      "installed": False}, indent=2))


if __name__ == "__main__":
    main()
