#!/usr/bin/env python3
"""
Rewrite commit messages from Portuguese to English while preserving
original author/committer timestamps.

Usage:
    python3 scripts/reword-commits.py

This script is used as a git filter-branch --msg-filter.
Reads message from stdin, outputs English translation to stdout.
"""

import sys
import re


def reword_message(msg: str) -> str:
    """Apply Portuguese -> English replacements to a commit message."""
    # Full-phrase replacements first (more specific)
    replacements = [
        # "adicionar" -> "add"
        (r"\badicionar\b", "add"),
        # "validação em 3 camadas" -> "validation in 3 layers"
        (r"validação em 3 camadas", "validation in 3 layers"),
        # "com validação" -> "with validation"
        (r"com validação", "with validation"),
        # "imagem customizada" -> "custom image"
        (r"imagem customizada", "custom image"),
        # "prevenir fechamento da janela" -> "prevent window closing"
        (r"prevenir fechamento da janela", "prevent window closing"),
        # "shutdown limpo do processo" -> "clean process shutdown"
        (r"shutdown limpo do processo", "clean process shutdown"),
        # "evita NSUInteger" -> "avoiding NSUInteger"
        (r"evita NSUInteger", "avoiding NSUInteger"),
        # "setado no evento Opened da janela" -> "set in the Opened event"
        (r"setado no evento Opened da janela", "set in the Opened event"),
        # "cria .app bundle com" -> "creates .app bundle with"
        (r"cria .app bundle com", "creates .app bundle with"),
        # "usando NSData+NSImage nativo com GCHandle" -> "using native NSData+NSImage with GCHandle"
        (r"usando NSData\+NSImage nativo com GCHandle", "using native NSData\+NSImage with GCHandle"),
        # "via API nativa" -> "via native API"
        (r"via API nativa", "via native API"),
        # "clique no Tray abre janela" -> "Tray click opens window"
        (r"clique no Tray abre janela", "Tray click opens window"),
        # "Start/Stop no menu do Tray" -> "Start/Stop in Tray menu"
        (r"Start/Stop no menu do Tray", "Start/Stop in Tray menu"),
        # "window icon, Tray click opens window, Start/Stop in Tray menu" (already English)
        # "icone" -> "icon" (standalone, before "of the" replacements)
        (r"\bicone\b", "icon"),
        # "no macOS" -> "on macOS"
        (r"no macOS", "on macOS"),
        # "do Dock" -> "of the Dock"
        (r"do Dock", "of the Dock"),
        # "do Dock/Cmd+Tab" -> "of the Dock/Cmd+Tab"
        (r"do Dock/Cmd\+Tab", "of the Dock/Cmd\+Tab"),
        # "na raiz do projeto para" -> "at project root to"
        (r"na raiz do projeto para", "at project root to"),
        # "funcionar na publish macOS" -> "work in macOS publish"
        (r"funcionar na publish macOS", "work in macOS publish"),
        # "remove all P/Invoke code and icon workarounds" (fix "code P/Invoke e icon")
        (r"remove all code P/Invoke e icon workarounds", "remove all P/Invoke code and icon workarounds"),
        # "usar dotnet publish para macOS" -> "use dotnet publish for macOS"
        (r"usar dotnet publish para macOS", "use dotnet publish for macOS"),
        # "com ícone" -> "with icon"
        (r"com ícone", "with icon"),
        # "e clean process" -> "and clean process"
        (r"\be clean\b", "and clean"),
        # "nativo" -> "native"
        (r"\bnativo\b", "native"),
        # "usando" -> "using"
        (r"\busando\b", "using"),
        # "cria" -> "creates"
        (r"\bcria\b", "creates"),
    ]

    for pattern, replacement in replacements:
        msg = re.sub(pattern, replacement, msg)

    # Fix double spaces
    msg = re.sub(r"  +", " ", msg)

    return msg


def main():
    # Read message from stdin (git filter-branch --msg-filter passes message via stdin)
    msg = sys.stdin.read().rstrip("\n")
    new_msg = reword_message(msg)
    print(new_msg)


if __name__ == "__main__":
    main()
