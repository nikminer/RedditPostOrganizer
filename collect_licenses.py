"""Copy dependency licenses into a release directory."""
from importlib.metadata import distributions
from pathlib import Path
import shutil
import sys

destination = Path(sys.argv[1]) / "licenses"
destination.mkdir(parents=True, exist_ok=True)
for distribution in distributions():
    name = distribution.metadata.get("Name", "package")
    for entry in distribution.files or []:
        if ".dist-info/" not in str(entry):
            continue
        if not any(token in entry.name.lower() for token in ("license", "copying", "notice")):
            continue
        original = Path(distribution.locate_file(entry))
        if original.is_file():
            target = destination / name / entry.name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(original, target)
