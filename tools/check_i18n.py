"""Prüft, ob alle im Code verwendeten Übersetzungsschlüssel in DE und EN vorhanden sind.

Aufruf:  python tools/check_i18n.py            → listet fehlende Schlüssel
         python tools/check_i18n.py --unused   → zusätzlich unbenutzte Schlüssel
Dynamische Schlüssel (z. B. L[$"cat.{key}"]) werden über die Präfixliste DYNAMIC berücksichtigt.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
LOC = ROOT / "src" / "BudgetPlaner.Shared" / "Localization"
SOURCES = [ROOT / "src" / "BudgetPlaner.Client", ROOT / "src" / "BudgetPlaner.Application", ROOT / "src" / "BudgetPlaner.Server"]

PATTERNS = [
    re.compile(r'\bL\["([a-zA-Z0-9_.\-]+)"\]'),
    re.compile(r'\bL\.T\("([a-zA-Z0-9_.\-]+)"'),
    re.compile(r'\bl\["([a-zA-Z0-9_.\-]+)"\]'),
    re.compile(r'\bl\.T\("([a-zA-Z0-9_.\-]+)"'),
    re.compile(r'Texts\.Get\([^,]+,\s*"([a-zA-Z0-9_.\-]+)"'),
    re.compile(r'Texts\.Get\(lang,\s*"([a-zA-Z0-9_.\-]+)"'),
    re.compile(r'AppException\.\w+\("([a-zA-Z0-9_.\-]+)"'),
    re.compile(r'\bthis\["([a-zA-Z0-9_.\-]+)"\]'),
    re.compile(r'(?<![\w.])T\("([a-z][a-zA-Z0-9_\-]*\.[a-zA-Z0-9_.\-]+)"'),
    re.compile(r'Texts\.Get\(\w+,\s*"([a-zA-Z0-9_.\-]+)"'),
]

DYNAMIC = ["cat.", "rhythm.", "kind.", "group.", "debttype.", "role.", "ampel.", "channel.", "notiftype.", "glossary.", "faq.",
           "landing.area.", "landing.how.", "theme.", "calc.", "help.", "onboarding.", "release.", "import.field.", "import.target.", "legal."]


DUPLICATES = []


def load(lang):
    data, origin = {}, {}
    for f in sorted(LOC.glob(f"*.{lang}.json")):
        for k, v in json.loads(f.read_text(encoding="utf-8")).items():
            if k in origin:
                DUPLICATES.append(f"{lang}: {k} ({origin[k]} / {f.name})")
            origin[k] = f.name
            data[k] = v
    return data


def main():
    de, en = load("de"), load("en")
    used = set()
    for src in SOURCES:
        for f in src.rglob("*"):
            if f.suffix not in (".razor", ".cs") or "obj" in f.parts or "bin" in f.parts:
                continue
            text = f.read_text(encoding="utf-8")
            for p in PATTERNS:
                used.update(p.findall(text))
    used = {k for k in used if not k.endswith(".")}  # dynamische Präfixe ("cat." + key)
    missing_de = sorted(k for k in used if k not in de)
    missing_en = sorted(k for k in used if k not in en)
    only_de = sorted(k for k in de if k not in en)
    only_en = sorted(k for k in en if k not in de)
    print(f"Schlüssel verwendet: {len(used)} | DE: {len(de)} | EN: {len(en)}")
    for title, items in (("Fehlt in DE", missing_de), ("Fehlt in EN", missing_en), ("Nur DE", only_de), ("Nur EN", only_en), ("Doppelt definiert", DUPLICATES)):
        if items:
            print(f"\n{title} ({len(items)}):")
            for k in items:
                print("  " + k)
    if "--unused" in sys.argv:
        unused = sorted(k for k in de if k not in used and not any(k.startswith(d) for d in DYNAMIC))
        print(f"\nUnbenutzt ({len(unused)}):")
        for k in unused:
            print("  " + k)
    return 1 if missing_de or missing_en or only_de or only_en or DUPLICATES else 0


if __name__ == "__main__":
    sys.exit(main())
