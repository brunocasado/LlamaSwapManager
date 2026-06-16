#!/usr/bin/env python3
"""
Test script para validar o fluxo de detecção de versão e update.
Simula o que o C# faz: version detection, regex parsing, version comparison.

Uso: python3 scripts/test_update_detection.py
"""
import re
import subprocess
import sys
import os
from dataclasses import dataclass
from typing import Optional

# ---- Config ----
LLAMA_CPP_DIR = os.path.expanduser("~/.llama")
LLAMA_SWAP_DIR = os.path.expanduser("~/.llama-swap")

# Fetch latest from GitHub at runtime
try:
    import urllib.request, json
    resp = urllib.request.urlopen("https://api.github.com/repos/ggml-org/llama.cpp/releases/latest", timeout=10)
    data = json.loads(resp.read())
    GITHUB_LLAMA_CPP_LATEST = data.get("tag_name", "b9660")
except Exception:
    GITHUB_LLAMA_CPP_LATEST = "b9660"  # fallback known value
GITHUB_LLAMA_SWAP_LATEST = "v226"  # Atualizar com o latest do GitHub


@dataclass
class VersionResult:
    has_update: bool
    remote_version: Optional[str]
    local_version: Optional[str]
    error: Optional[str] = None


def detect_llama_cpp_version(target_dir: str) -> Optional[str]:
    """Simula DetectLocalVersion do LlamaCppDownloader.cs — NOVA prioridade"""
    server_path = os.path.join(target_dir, "llama-server")
    if not os.path.exists(server_path):
        if os.name == 'nt':
            server_path += ".exe"
            if not os.path.exists(server_path):
                return None
        else:
            return None

    try:
        result = subprocess.run(
            [server_path, "--version"],
            capture_output=True, text=True, timeout=5
        )
        output = result.stdout + result.stderr

        # Priority 1: decimal build number → b9553
        m = re.search(r'version:\s*(\d+)', output)
        if m:
            return f"b{m.group(1)}"

        # Priority 2: commit hash fallback
        m = re.search(r'\(([0-9a-fA-F]{5,})\)', output)
        if m:
            return f"b{m.group(1)[:5]}"

        return None
    except Exception:
        return None


def detect_llama_swap_version(dir_path: str) -> Optional[str]:
    """Simula DetectCurrentVersionAsync do UpdateViewModel.cs"""
    binary_path = os.path.join(dir_path, "llama-swap")
    if not os.path.exists(binary_path):
        return None

    try:
        result = subprocess.run(
            [binary_path, "--version"],
            capture_output=True,
            text=True,
            timeout=5
        )
        output = result.stdout

        # Regex: version: 223 → v223
        m = re.search(r"version:\s*(\d+)", output)
        if m:
            return f"v{m.group(1)}"

        return None
    except Exception as e:
        print(f"  [ERROR] {e}")
        return None


def compare_versions(local: str, remote: str) -> bool:
    """Simula VersionComparer.HasUpdate — igual ao C#: decimal para bXXX, numeric para vXXX"""
    if local is None or remote is None:
        return False
    # Para llama.cpp (bXXX): compara como DECIMAL (não hex!)
    if local.startswith("b") and remote.startswith("b"):
        local_num = local[1:]
        remote_num = remote[1:]
        # Tenta como decimal primeiro
        try:
            return int(local_num) < int(remote_num)
        except ValueError:
            # Se não for decimal, fallback para string
            return local != remote
    # Para llama-swap (vXXX): compara numericamente
    local_num = local.lstrip("v")
    remote_num = remote.lstrip("v")
    try:
        local_parts = [int(p) for p in local_num.split(".")]
        remote_parts = [int(p) for p in remote_num.split(".")]
        max_len = max(len(local_parts), len(remote_parts))
        local_parts.extend([0] * (max_len - len(local_parts)))
        remote_parts.extend([0] * (max_len - len(remote_parts)))
        return local_parts < remote_parts
    except ValueError:
        return local != remote


def test(name: str, condition: bool, expected: bool = True):
    status = "✅" if condition == expected else "❌"
    print(f"  {status} {name}")
    return condition == expected


def main():
    print("=" * 60)
    print("TESTE: Detecção de Versão e Update")
    print("=" * 60)

    all_passed = True

    # ---- Test 1: llama.cpp version detection ----
    print("\n1. llama.cpp version detection")
    print(f"   Binary: {LLAMA_CPP_DIR}/llama-server")

    local_cpp = detect_llama_cpp_version(LLAMA_CPP_DIR)
    print(f"   Local detectado: {local_cpp}")

    # Verificar que o binary existe e roda
    cpp_binary_exists = os.path.exists(os.path.join(LLAMA_CPP_DIR, "llama-server"))
    all_passed &= test("Binary llama-server existe", cpp_binary_exists, True)

    # Verificar que o regex extrai o hash
    has_hash = local_cpp is not None and local_cpp.startswith("b")
    all_passed &= test("Regex extrai hash (bXXXXX)", has_hash, True)

    # Verificar que a versão é diferente do latest
    cpp_has_update = compare_versions(local_cpp, GITHUB_LLAMA_CPP_LATEST) if local_cpp else False
    print(f"   Remote (GitHub): {GITHUB_LLAMA_CPP_LATEST}")
    print(f"   HasUpdate: {cpp_has_update}")
    all_passed &= test("HasUpdate detectado", cpp_has_update, True)

    # ---- Test 2: llama-swap version detection ----
    print("\n2. llama-swap version detection")
    print(f"   Binary: {LLAMA_SWAP_DIR}/llama-swap")

    local_swap = detect_llama_swap_version(LLAMA_SWAP_DIR)
    print(f"   Local detectado: {local_swap}")

    # Verificar que o binary existe e roda
    swap_binary_exists = os.path.exists(os.path.join(LLAMA_SWAP_DIR, "llama-swap"))
    all_passed &= test("Binary llama-swap existe", swap_binary_exists, True)

    # Verificar que o regex extrai o build number
    has_build = local_swap is not None and local_swap.startswith("v")
    all_passed &= test("Regex extrai build number (vXXX)", has_build, True)

    # Verificar que a versão é diferente do latest
    swap_has_update = compare_versions(local_swap, GITHUB_LLAMA_SWAP_LATEST) if local_swap else False
    print(f"   Remote (GitHub): {GITHUB_LLAMA_SWAP_LATEST}")
    print(f"   HasUpdate: {swap_has_update}")
    all_passed &= test("HasUpdate detectado", swap_has_update, True)

    # ---- Test 3: Version comparison edge cases ----
    print("\n3. Edge cases de version comparison")

    # Hash vs hash (mesmo formato) — diferente = tem update
    # Agora usa decimal: b9e3b9 → inválido (contém 'e'), fallback para string
    all_passed &= test(
        "b9e3b9 vs b9654 → diferente (fallback string)",
        compare_versions("b9e3b9", "b9654") == True,
        True
    )

    # Decimal vs decimal — mesmo = sem update
    all_passed &= test(
        "b9660 vs b9660 → igual",
        compare_versions("b9660", "b9660") == False,
        True
    )

    # Decimal vs decimal — local mais novo = sem update
    all_passed &= test(
        "b9661 vs b9660 → sem update (local mais novo)",
        compare_versions("b9661", "b9660") == False,
        True
    )

    # Decimal vs decimal — local mais antigo = tem update
    all_passed &= test(
        "b9553 vs b9660 → update",
        compare_versions("b9553", "b9660") == True,
        True
    )

    # Mesmo version
    all_passed &= test(
        "v226 vs v226 → sem update",
        compare_versions("v226", "v226") == False,
        True
    )

    # ---- Test 4: chmod/xattr paths ----
    print("\n4. Paths de utilitários macOS")

    chmod_path = "/bin/chmod"
    xattr_path = "/usr/bin/xattr"

    all_passed &= test(
        f"/bin/chmod existe",
        os.path.exists(chmod_path),
        True
    )

    all_passed &= test(
        f"/usr/bin/xattr existe",
        os.path.exists(xattr_path),
        True
    )

    # ---- Result ----
    print("\n" + "=" * 60)
    if all_passed:
        print("✅ TODOS OS TESTES PASSARAM")
        print("   O fluxo de version detection está funcionando.")
    else:
        print("❌ ALGUNS TESTES FALHARAM")
        print("   Revise os erros acima.")
    print("=" * 60)

    return 0 if all_passed else 1


if __name__ == "__main__":
    sys.exit(main())