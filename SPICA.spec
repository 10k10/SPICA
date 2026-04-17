# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec for SPICA — Spice Parallel Instance Circuit Analyzer
#
# Build command (from repo root):
#   pyinstaller SPICA.spec
#
# Output: dist/SPICA/  (one-dir bundle, run SPICA.exe inside)

from PyInstaller.utils.hooks import collect_data_files, collect_all
import sys
from pathlib import Path

block_cipher = None

# ── Collect data files from packages that need runtime resources
datas = []
datas += collect_data_files("customtkinter")
datas += collect_data_files("spicelib")
datas += [("templates", "templates")]          # Jinja2 HTML template

# scipy, reportlab ship compiled extensions — collect_all grabs everything
hiddenimports = []
scipy_datas, scipy_bins, scipy_hidden = collect_all("scipy")
datas    += scipy_datas
hiddenimports += scipy_hidden

rl_datas, rl_bins, rl_hidden = collect_all("reportlab")
datas    += rl_datas
hiddenimports += rl_hidden

# sklearn (scikit-learn) — used in analyzer.py
skl_datas, skl_bins, skl_hidden = collect_all("sklearn")
datas    += skl_datas
hiddenimports += skl_hidden

a = Analysis(
    ["main.py"],
    pathex=["."],
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports + [
        "customtkinter",
        "spicelib",
        "spicelib.log.ltsteps",
        "matplotlib",
        "matplotlib.backends.backend_agg",
        "jinja2",
        "numpy",
        "scipy",
        "sklearn",
        "reportlab",
        "PIL",
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=["weasyprint", "xhtml2pdf", "tkinter.test"],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="SPICA",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,          # windowed — no terminal window
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=None,              # add icon.ico here if you have one
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="SPICA",
)
