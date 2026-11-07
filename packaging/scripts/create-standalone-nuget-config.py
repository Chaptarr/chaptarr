#!/usr/bin/env python3
"""Add the selected extra-platform runtime packages to a NuGet config."""

from pathlib import Path
import re
import sys
from xml.sax.saxutils import quoteattr


SUPPORTED_RIDS = {"linux-x86", "freebsd-x64"}
SOURCE_NAME = "chaptarr-extra-runtime-packs"


def main() -> None:
    if len(sys.argv) != 5:
        raise SystemExit(
            "Usage: create-standalone-nuget-config.py "
            "<runtime-pack-directory> <source-config> <output-config> <rid>"
        )

    runtime_packs, source_config, output_config = map(Path, sys.argv[1:4])
    rid = sys.argv[4]
    if rid not in SUPPORTED_RIDS:
        raise SystemExit(f"Unsupported extra-platform runtime identifier: {rid}")
    if not runtime_packs.is_dir():
        raise SystemExit(f"Runtime-pack directory does not exist: {runtime_packs}")

    # This generator only accepts the checked-in NuGet config, never arbitrary XML.
    expected_config = Path(__file__).resolve().parents[2] / "src" / "NuGet.config"
    if source_config.resolve() != expected_config.resolve():
        raise SystemExit(f"Source config must be the repository NuGet config: {expected_config}")
    if output_config.resolve() == expected_config.resolve():
        raise SystemExit("The generated config must not overwrite the repository NuGet config")

    raw_config = source_config.read_bytes()
    bom = b"\xef\xbb\xbf" if raw_config.startswith(b"\xef\xbb\xbf") else b""
    try:
        config_text = raw_config[len(bom):].decode("utf-8")
    except UnicodeDecodeError as exc:
        raise SystemExit(f"The source NuGet config is not UTF-8: {source_config}") from exc

    for section in ("packageSources", "packageSourceMapping"):
        for marker in (f"<{section}>", f"</{section}>"):
            if len(re.findall(rf"(?m)^[ \t]*{re.escape(marker)}[ \t]*$", config_text)) != 1:
                raise SystemExit(f"The source NuGet config must define one {section} section")
    if f'key="{SOURCE_NAME}"' in config_text:
        raise SystemExit("The custom runtime source is already present")
    if not any(
        '<packageSource key="nuget.org">' in section and '<package pattern="*"' in section
        for section in config_text.split("</packageSource>")
    ):
        raise SystemExit("The source NuGet config must retain the nuget.org wildcard package mapping")

    def insert_before_section_close(text: str, closing_tag: str, lines: list[str]) -> str:
        marker_index = text.index(closing_tag)
        line_start = text.rfind("\n", 0, marker_index) + 1
        indentation = text[line_start:marker_index]
        insertion = "".join(f"{indentation}{line}\n" for line in lines)
        return text[:line_start] + insertion + text[line_start:]

    source_entry = f'<add key="{SOURCE_NAME}" value={quoteattr(str(runtime_packs.resolve()))} />'
    config_text = insert_before_section_close(config_text, "</packageSources>", [source_entry])

    if f'<packageSource key="{SOURCE_NAME}"' in config_text:
        raise SystemExit("The custom runtime package mapping is already present")
    package_ids = (
        f"Microsoft.NETCore.App.Host.{rid}",
        f"Microsoft.NETCore.App.Runtime.{rid}",
        f"Microsoft.AspNetCore.App.Runtime.{rid}",
    )
    mapping_entry = [
        f'<packageSource key="{SOURCE_NAME}">',
        *(f'<package pattern="{package_id}" />' for package_id in package_ids),
        "</packageSource>",
    ]
    config_text = insert_before_section_close(config_text, "</packageSourceMapping>", mapping_entry)

    output_config.parent.mkdir(parents=True, exist_ok=True)
    output_config.write_bytes(bom + config_text.encode("utf-8"))


if __name__ == "__main__":
    main()
