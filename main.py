"""
SPICA — Spice Parallel Instance Circuit Analyzer
Entry point. Lança a UI CustomTkinter. Toda lógica pesada roda em threads separadas.
"""
import sys
from pathlib import Path

# Garante que o pacote é encontrado independentemente de como foi invocado
sys.path.insert(0, str(Path(__file__).parent))

import customtkinter as ctk
from ui.app import App

if __name__ == "__main__":
    ctk.set_appearance_mode("dark")
    ctk.set_default_color_theme("blue")
    app = App()
    app.mainloop()
