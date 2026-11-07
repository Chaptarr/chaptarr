#!/usr/bin/env python3
"""One-time, recoverable BookshelfNG -> ChaptarrNG handoff for manual systemd installs."""

from __future__ import annotations

import argparse
import fcntl
import hashlib
import http.client
import ipaddress
import json
import os
import pathlib
import platform
import pwd
import grp
import re
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET


SCHEMA_VERSION = 1
MAX_HEALTH_TIMEOUT = 14700
SYSTEMD_STARTUP_MARGIN_SECONDS = 180
SYSTEMD_STOP_TIMEOUT_SECONDS = 30600
HOOK_TIMEOUT = 20
SYSTEMD_DROPIN_NAME = "90-chaptarrng-bookshelf-handoff.conf"
SYSTEMD_PREFIX = "/etc/systemd/system"
SYSTEMD_ENV_KEYS_TO_DROP = {
    "INVOCATION_ID",
    "JOURNAL_STREAM",
    "NOTIFY_SOCKET",
    "WATCHDOG_PID",
    "WATCHDOG_USEC",
    "MAINPID",
    "SYSTEMD_EXEC_PID",
}
BOOTSTRAP_ENVIRONMENT: dict[str, str] | None = None


class HandoffError(RuntimeError):
    pass


class BookshelfConfigTreeBuilder(ET.TreeBuilder):
    def __init__(self, *, insert_comments: bool = False) -> None:
        super().__init__(insert_comments=insert_comments)

    def doctype(self, name: str, pubid: str | None, system: str | None) -> None:
        raise HandoffError("BookshelfNG config.xml must not contain a document type declaration.")


def parse_config_xml(path: pathlib.Path, *, insert_comments: bool = False) -> ET.ElementTree:
    parser = ET.XMLParser(target=BookshelfConfigTreeBuilder(insert_comments=insert_comments))
    # DTDs are rejected by BookshelfConfigTreeBuilder.doctype before parsing continues.
    return ET.parse(path, parser=parser)  # nosemgrep: python.lang.security.use-defused-xml-parse.use-defused-xml-parse


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def run_command(command: list[str], *, timeout: int = 60, quiet: bool = False) -> subprocess.CompletedProcess[str]:
    try:
        result = subprocess.run(
            command,
            check=False,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise HandoffError(f"Could not run {pathlib.Path(command[0]).name}.") from exc
    if result.returncode != 0 and not quiet:
        detail = (result.stderr or result.stdout).strip().splitlines()
        if detail:
            log(detail[-1][:500])
    return result


def systemctl_value(property_name: str, service: str) -> str:
    result = run_command(["systemctl", "show", f"--property={property_name}", "--value", service])
    if result.returncode != 0:
        raise HandoffError(f"systemd could not read {property_name} for {service}.")
    return result.stdout.strip()


def service_active(service: str) -> bool:
    return run_command(["systemctl", "is-active", "--quiet", service], quiet=True).returncode == 0


def service_starting_or_active(service: str) -> bool:
    return systemctl_value("ActiveState", service) in ("activating", "active", "reloading")


def service_main_pid(service: str) -> int:
    raw = systemctl_value("MainPID", service)
    try:
        return int(raw)
    except ValueError as exc:
        raise HandoffError(f"systemd returned an invalid MainPID for {service}.") from exc


def validate_service_name(value: str) -> str:
    if not re.fullmatch(r"[A-Za-z0-9_.@-]+\.service", value):
        raise HandoffError("--service must be a simple systemd unit name ending in .service.")
    return value


def acquire_service_handoff_lock(service: str) -> int:
    lock_id = hashlib.sha256(service.encode("utf-8")).hexdigest()[:32]
    lock_path = pathlib.Path("/run/lock") / f"chaptarrng-bookshelf-handoff-{lock_id}.lock"
    flags = os.O_RDWR | os.O_CREAT
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    try:
        descriptor = os.open(lock_path, flags, 0o600)
    except OSError as exc:
        raise HandoffError("The per-service BookshelfNG handoff lock could not be opened safely.") from exc

    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) & 0o077:
            raise HandoffError("The per-service BookshelfNG handoff lock has unsafe ownership or permissions.")
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as exc:
            raise HandoffError(f"Another ChaptarrNG handoff is already running for {service}.") from exc
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def absolute_directory(value: str, label: str, *, must_exist: bool) -> pathlib.Path:
    path = pathlib.Path(value)
    if not path.is_absolute() or any(part == ".." for part in path.parts):
        raise HandoffError(f"{label} must be an absolute path without '..'.")
    if must_exist:
        if path.is_symlink() or not path.is_dir():
            raise HandoffError(f"{label} must be an existing real directory: {path}")
        return path.resolve(strict=True)
    parent = path.parent.resolve(strict=True)
    resolved = parent / path.name
    if resolved.exists() or resolved.is_symlink():
        raise HandoffError(f"{label} must not already exist: {resolved}")
    return resolved


def require_private_root_parent(path: pathlib.Path) -> None:
    parent_stat = path.parent.stat()
    if parent_stat.st_uid != 0 or stat.S_IMODE(parent_stat.st_mode) & 0o022:
        raise HandoffError("--backup-dir's existing parent must be root-owned and not group- or world-writable.")


def verify_release_archive(archive: pathlib.Path) -> tuple[str, int, str]:
    name_match = re.fullmatch(r"ChaptarrNG-(\d+\.\d+\.\d+)-linux-(x64|arm64)\.tar\.gz", archive.name)
    if not name_match:
        raise HandoffError("--archive must be a versioned ChaptarrNG Linux x64 or arm64 release archive.")
    machine = platform.machine().lower()
    expected_arch = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(machine)
    if expected_arch != name_match.group(2):
        raise HandoffError(f"The archive targets linux-{name_match.group(2)}, but this host is {machine}.")
    sidecar = pathlib.Path(str(archive) + ".sha256")
    if not sidecar.is_file() or sidecar.is_symlink():
        raise HandoffError(f"The checksum sidecar is missing or unsafe: {sidecar}")
    expected = None
    for line in sidecar.read_text(encoding="utf-8").splitlines():
        parts = line.strip().split(None, 1)
        if len(parts) == 2 and parts[1].lstrip("*") == archive.name and re.fullmatch(r"[a-fA-F0-9]{64}", parts[0]):
            if expected is not None:
                raise HandoffError("The checksum sidecar contains duplicate entries for this archive.")
            expected = parts[0].lower()
    if expected is None:
        raise HandoffError("The checksum sidecar has no SHA-256 entry for this archive.")
    digest = hashlib.sha256()
    with archive.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != expected:
        raise HandoffError("The release archive does not match its SHA-256 sidecar.")
    with tarfile.open(archive, "r:gz") as package:
        members = package.getmembers()
        total_size = 0
        names: set[str] = set()
        for member in members:
            relative = pathlib.PurePosixPath(member.name)
            if relative.is_absolute() or ".." in relative.parts or not relative.parts or relative.parts[0] != "ChaptarrNG":
                raise HandoffError(f"The release archive contains an unsafe path: {member.name}")
            if member.name in names:
                raise HandoffError(f"The release archive contains a duplicate path: {member.name}")
            names.add(member.name)
            if member.issym() or member.islnk() or not (member.isdir() or member.isfile()):
                raise HandoffError(f"The release archive contains an unsupported entry: {member.name}")
            if member.isfile():
                total_size += member.size
        if "ChaptarrNG/Chaptarr" not in names:
            raise HandoffError("The release archive does not contain the Chaptarr executable.")
    return name_match.group(1), total_size, digest.hexdigest()


def copy_private_regular_file(source: pathlib.Path, destination: pathlib.Path) -> None:
    source_flags = os.O_RDONLY
    if hasattr(os, "O_NOFOLLOW"):
        source_flags |= os.O_NOFOLLOW
    source_fd = os.open(source, source_flags)
    destination_fd = None
    destination_created = False
    try:
        if not stat.S_ISREG(os.fstat(source_fd).st_mode):
            raise HandoffError(f"The release input is not a regular file: {source.name}")
        destination_flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
        if hasattr(os, "O_NOFOLLOW"):
            destination_flags |= os.O_NOFOLLOW
        destination_fd = os.open(destination, destination_flags, 0o600)
        destination_created = True
        with os.fdopen(source_fd, "rb", closefd=True) as input_file:
            source_fd = -1
            with os.fdopen(destination_fd, "wb", closefd=True) as output_file:
                destination_fd = None
                shutil.copyfileobj(input_file, output_file)
                output_file.flush()
                os.fsync(output_file.fileno())
    except BaseException:
        if destination_created:
            try:
                destination.unlink()
            except FileNotFoundError:
                pass
        raise
    finally:
        if source_fd >= 0:
            os.close(source_fd)
        if destination_fd is not None:
            os.close(destination_fd)


def extract_release_archive(archive: pathlib.Path, destination: pathlib.Path) -> pathlib.Path:
    destination.mkdir(mode=0o755, parents=True)
    with tarfile.open(archive, "r:gz") as package:
        for member in package.getmembers():
            relative = pathlib.PurePosixPath(member.name)
            target = destination.joinpath(*relative.parts)
            if member.isdir():
                target.mkdir(mode=0o755, parents=True, exist_ok=True)
            else:
                target.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
                source = package.extractfile(member)
                if source is None:
                    raise HandoffError(f"The release archive could not read {member.name}.")
                with source, target.open("xb") as output:
                    shutil.copyfileobj(source, output)
                os.chmod(target, member.mode & 0o755)
    app = destination / "ChaptarrNG"
    executable = app / "Chaptarr"
    if not executable.is_file() or not os.access(executable, os.X_OK):
        raise HandoffError("The extracted Chaptarr executable is missing or not executable.")
    return app


def read_process(pid: int) -> tuple[dict[str, str], list[str], pathlib.Path]:
    proc = pathlib.Path("/proc") / str(pid)
    try:
        raw_env = (proc / "environ").read_bytes()
        raw_argv = (proc / "cmdline").read_bytes()
        executable = pathlib.Path(os.readlink(proc / "exe").removesuffix(" (deleted)"))
    except OSError as exc:
        raise HandoffError("The running BookshelfNG process environment could not be read safely.") from exc
    environment: dict[str, str] = {}
    for item in raw_env.split(b"\0"):
        if b"=" not in item:
            continue
        key, value = item.split(b"=", 1)
        environment[os.fsdecode(key)] = os.fsdecode(value)
    argv = [os.fsdecode(part) for part in raw_argv.split(b"\0") if part]
    if not argv:
        raise HandoffError("The running BookshelfNG process has no readable command line.")
    for key in SYSTEMD_ENV_KEYS_TO_DROP:
        environment.pop(key, None)
    return environment, argv, executable


def resolve_service_identity(service: str) -> tuple[int, int, list[int], str, str]:
    dynamic = systemctl_value("DynamicUser", service).lower()
    private_users = systemctl_value("PrivateUsers", service).lower()
    if dynamic == "yes" or private_users == "yes":
        raise HandoffError("DynamicUser and PrivateUsers services need a platform-specific migration package.")
    user_value = systemctl_value("User", service)
    group_value = systemctl_value("Group", service)
    if user_value:
        try:
            account = pwd.getpwuid(int(user_value)) if user_value.isdigit() else pwd.getpwnam(user_value)
        except KeyError as exc:
            raise HandoffError("The BookshelfNG service account could not be resolved.") from exc
        uid = account.pw_uid
        username = account.pw_name
    else:
        account = pwd.getpwuid(0)
        uid = 0
        username = account.pw_name
    if group_value:
        try:
            group = grp.getgrgid(int(group_value)) if group_value.isdigit() else grp.getgrnam(group_value)
        except KeyError as exc:
            raise HandoffError("The BookshelfNG service group could not be resolved.") from exc
        gid = group.gr_gid
        group_name = group.gr_name
    else:
        gid = account.pw_gid
        group_name = grp.getgrgid(gid).gr_name
    groups = set(os.getgrouplist(username, gid))
    for supplementary_group in systemctl_value("SupplementaryGroups", service).split():
        try:
            group = grp.getgrgid(int(supplementary_group)) if supplementary_group.isdigit() else grp.getgrnam(supplementary_group)
        except KeyError as exc:
            raise HandoffError("A configured BookshelfNG supplementary group could not be resolved.") from exc
        groups.add(group.gr_gid)
    groups = sorted(groups)
    return uid, gid, groups, username, group_name


def parse_option(arguments: list[str], name: str, *, bare_boolean: bool = False) -> str | None:
    known_switches = {
        "data", "port", "urlbase", "bindaddress", "sslport", "enablessl",
        "sslcertpath", "sslcertpassword", "i", "u", "?", "nobrowser", "nostart",
        "installservice", "uninstallservice", "terminateexisting", "restart", "registerurl",
        "nosingleinstancecheck", "exitimmediately", "migrate-bookshelf", "preview-bookshelf-migration",
        "merge-bookshelf-databases",
        "check-bookshelf-migration", "restore-bookshelf", "preview-bookshelf-restore",
        "check-bookshelf-postgres-backup", "preview-bookshelf-postgres-restore",
        "backup-bookshelf-postgres", "restore-bookshelf-postgres",
        "confirm-bookshelf-service-stopped",
    }

    def is_switch(value: str) -> bool:
        if value.startswith("-"):
            return True
        if value.startswith("/"):
            normalized_value = value.lstrip("/").casefold()
            if normalized_value == "data" and name.casefold() in ("data", "urlbase"):
                return False
            return any(
                normalized_value == option or normalized_value.startswith(option + "=")
                for option in known_switches
            )
        return False

    values: list[str] = []
    index = 0
    while index < len(arguments):
        argument = arguments[index]
        normalized = argument.lstrip("-/")
        if normalized.lower().startswith(name.lower() + "="):
            values.append(normalized.split("=", 1)[1])
        elif normalized.lower() == name.lower():
            if index + 1 >= len(arguments) or is_switch(arguments[index + 1]):
                if not bare_boolean:
                    raise HandoffError(f"The BookshelfNG service option -{name} is missing its value.")
                values.append("true")
            else:
                value = arguments[index + 1]
                if bare_boolean:
                    if value.casefold() not in ("true", "false"):
                        raise HandoffError(f"The BookshelfNG service option -{name} must be true or false when given a value.")
                    values.append(value.casefold())
                else:
                    values.append(value)
                index += 1
        index += 1

    if not values:
        return None
    if any(value != values[0] for value in values[1:]):
        raise HandoffError(f"The BookshelfNG service has conflicting -{name} options; resolve them before migration.")
    return values[0]


def command_line_server_overrides(argv: list[str]) -> dict[str, str]:
    option_names = {
        "urlbase": "UrlBase",
        "bindaddress": "BindAddress",
        "sslport": "SslPort",
        "enablessl": "EnableSsl",
        "sslcertpath": "SslCertPath",
        "sslcertpassword": "SslCertPassword",
    }
    overrides = {}
    for option_name, config_name in option_names.items():
        value = parse_option(argv[1:], option_name, bare_boolean=option_name == "enablessl")
        if value is not None:
            overrides[config_name] = value
    return overrides


def persist_target_server_settings(data_dir: pathlib.Path, port: int, argv: list[str], state: dict) -> None:
    config_file = data_dir / "config.xml"
    if config_file.is_symlink():
        raise HandoffError("config.xml became a symlink before target settings were preserved.")

    if config_file.is_file():
        try:
            tree = parse_config_xml(config_file, insert_comments=True)
        except (ET.ParseError, OSError, HandoffError) as exc:
            raise HandoffError("BookshelfNG config.xml could not be read while preserving its server settings.") from exc
        root = tree.getroot()
        original_stat = config_file.stat()
        file_uid = original_stat.st_uid
        file_gid = original_stat.st_gid
        file_mode = stat.S_IMODE(original_stat.st_mode)
    else:
        root = ET.Element("Config")
        tree = ET.ElementTree(root)
        file_uid = int(state["configUid"])
        file_gid = int(state["configGid"])
        file_mode = int(state["configMode"])

    values = {"Port": str(port), **command_line_server_overrides(argv)}
    for name, value in values.items():
        matches = [
            element for element in root.iter()
            if isinstance(element.tag, str) and element.tag.rsplit("}", 1)[-1].lower() == name.lower()
        ]
        if len(matches) > 1:
            raise HandoffError(f"BookshelfNG config.xml contains duplicate {name} settings; refusing an ambiguous server handoff.")
        element = matches[0] if matches else ET.SubElement(root, name)
        element.text = value

    temporary = config_file.with_name(f".{config_file.name}.bookshelfng-{os.getpid()}.tmp")
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    descriptor = None
    try:
        descriptor = os.open(temporary, flags, file_mode)
        with os.fdopen(descriptor, "wb") as output:
            descriptor = None
            tree.write(output, encoding="utf-8", xml_declaration=True)
            output.flush()
            os.fsync(output.fileno())
        os.chown(temporary, file_uid, file_gid)
        os.chmod(temporary, file_mode)
        os.replace(temporary, config_file)
        directory_fd = os.open(data_dir, os.O_RDONLY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    except BaseException:
        if descriptor is not None:
            os.close(descriptor)
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass
        raise


def validate_service_data_directory(data_dir: pathlib.Path, argv: list[str]) -> None:
    configured = parse_option(argv[1:], "data")
    if configured is None:
        raise HandoffError(
            "The running BookshelfNG service must use an explicit -data path so the bootstrap can verify it matches --data-dir."
        )
    configured_path = pathlib.Path(configured)
    if not configured_path.is_absolute() or ".." in configured_path.parts:
        raise HandoffError("The BookshelfNG service's -data path must be absolute and must not contain '..'.")
    try:
        resolved_configured_path = configured_path.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise HandoffError("The BookshelfNG service's configured -data path could not be resolved.") from exc
    if resolved_configured_path != data_dir:
        raise HandoffError(
            "--data-dir does not match the running BookshelfNG service's -data path; no files or databases were changed."
        )


def config_value(config_file: pathlib.Path, key: str) -> str | None:
    if not config_file.is_file() or config_file.is_symlink():
        return None
    try:
        root = parse_config_xml(config_file).getroot()
    except (ET.ParseError, OSError, HandoffError):
        return None
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1].lower() == key.lower() and element.text:
            return element.text.strip()
    return None


def local_health_host(bind_address: str) -> str:
    value = bind_address.strip().strip("[]")
    if value in ("", "*", "+", "0.0.0.0"):
        return "127.0.0.1"
    if value == "::":
        return "::1"
    if value.lower() == "localhost":
        return "127.0.0.1"

    try:
        address = ipaddress.ip_address(value)
    except ValueError:
        resolved = {
            ipaddress.ip_address(item[4][0].split("%", 1)[0])
            for item in socket.getaddrinfo(value, None, type=socket.SOCK_STREAM)
        }
        if not resolved:
            raise HandoffError("The BookshelfNG bind name did not resolve to a local address.")
    else:
        resolved = {address}

    if all(address.is_loopback for address in resolved):
        return str(next(iter(resolved)))

    result = run_command(["ip", "-o", "address", "show"], quiet=True)
    if result.returncode != 0:
        raise HandoffError("The configured BookshelfNG bind address cannot be verified as local because the ip command is unavailable.")
    local_addresses = set()
    for line in result.stdout.splitlines():
        match = re.search(r"\b(?:inet6?)\s+([^/\s]+)", line)
        if match:
            try:
                local_addresses.add(ipaddress.ip_address(match.group(1).split("%", 1)[0]))
            except ValueError:
                continue
    if not resolved.issubset(local_addresses | {address for address in resolved if address.is_loopback}):
        raise HandoffError("The configured BookshelfNG bind address does not resolve only to local network interfaces.")
    return str(next(iter(resolved)))


def app_settings(data_dir: pathlib.Path, environment: dict[str, str], argv: list[str]) -> tuple[int, str, str]:
    config = data_dir / "config.xml"
    port_value = parse_option(argv[1:], "port")
    if port_value is None:
        port_value = next((environment[key] for key in (
            "Chaptarr__Server__Port", "CHAPTARR__SERVER__PORT",
            "Readarr__Server__Port", "READARR__SERVER__PORT",
            "Audioarr__Server__Port", "AUDIOARR__SERVER__PORT",
            "Bookshelf__Server__Port", "BOOKSHELF__SERVER__PORT", "BOOKSHELF_PORT",
        ) if environment.get(key)), None)
    if port_value is None:
        port_value = config_value(config, "Port") or environment.get("CHAPTARR_DEFAULT_PORT") or "8787"
    try:
        port = int(port_value)
    except (TypeError, ValueError) as exc:
        raise HandoffError("The BookshelfNG server port is not a valid integer.") from exc
    if port < 1 or port > 65535:
        raise HandoffError("The BookshelfNG server port is outside the valid range.")
    url_base = parse_option(argv[1:], "urlbase")
    if url_base is None:
        url_base = next((environment[key] for key in (
            "Chaptarr__Server__UrlBase", "CHAPTARR__SERVER__URLBASE",
            "Readarr__Server__UrlBase", "READARR__SERVER__URLBASE",
            "Audioarr__Server__UrlBase", "AUDIOARR__SERVER__URLBASE",
            "Bookshelf__Server__UrlBase", "BOOKSHELF__SERVER__URLBASE", "BOOKSHELF_URLBASE",
        ) if environment.get(key)), None)
    if url_base is None:
        url_base = config_value(config, "UrlBase") or ""
    bind_address = parse_option(argv[1:], "bindaddress")
    if bind_address is None:
        bind_address = next((environment[key] for key in (
            "Chaptarr__Server__BindAddress", "CHAPTARR__SERVER__BINDADDRESS",
            "Readarr__Server__BindAddress", "READARR__SERVER__BINDADDRESS",
            "Audioarr__Server__BindAddress", "AUDIOARR__SERVER__BINDADDRESS",
            "Bookshelf__Server__BindAddress", "BOOKSHELF__SERVER__BINDADDRESS",
        ) if environment.get(key)), None)
    if bind_address is None:
        bind_address = config_value(config, "BindAddress") or "*"
    health_host = local_health_host(bind_address)
    return port, "/" + url_base.strip("/") if url_base.strip("/") else "", health_host


def postgres_source(data_dir: pathlib.Path, environment: dict[str, str]) -> bool:
    for prefix in ("CHAPTARR", "READARR", "AUDIOARR", "BOOKSHELF"):
        key = f"{prefix}__POSTGRES__HOST".casefold()
        values = [value for name, value in environment.items() if name.casefold() == key]
        if values:
            return bool(values[-1].strip())
    return bool(config_value(data_dir / "config.xml", "PostgresHost"))


def sqlite_database_has_data(data_dir: pathlib.Path, database_name: str) -> bool:
    database = data_dir / database_name
    if database.is_symlink():
        raise HandoffError(f"The BookshelfNG database {database_name} is a symbolic link.")
    if database.exists() and not database.is_file():
        raise HandoffError(f"The BookshelfNG database {database_name} is not a regular file.")

    has_durable_sidecar = False
    for suffix in ("-journal", "-wal", "-shm"):
        sidecar = pathlib.Path(str(database) + suffix)
        if sidecar.is_symlink():
            raise HandoffError(f"The BookshelfNG SQLite sidecar {database_name + suffix} is a symbolic link.")
        if sidecar.exists() and not sidecar.is_file():
            raise HandoffError(f"The BookshelfNG SQLite sidecar {database_name + suffix} is not a regular file.")
        if suffix != "-shm" and sidecar.is_file() and sidecar.stat().st_size > 0:
            has_durable_sidecar = True

    if not database.exists():
        if has_durable_sidecar:
            raise HandoffError(f"The BookshelfNG database {database_name} is missing but its WAL or journal contains data.")
        return False

    return database.stat().st_size > 0 or has_durable_sidecar


def has_dual_populated_sqlite_sources(data_dir: pathlib.Path) -> bool:
    return sqlite_database_has_data(data_dir, "readarr.db") and sqlite_database_has_data(data_dir, "audioarr.db")


def migration_preview_arguments(state: dict) -> list[str]:
    arguments = ["-preview-bookshelf-migration"]
    if state.get("mergeSqliteSources"):
        arguments.append("-merge-bookshelf-databases")
    arguments.append(f"-data={state['dataDir']}")
    return arguments


def service_identity_from_state(state: dict) -> tuple[int, int, list[int]]:
    return int(state["uid"]), int(state["gid"]), [int(value) for value in state["groups"]]


def _drop_privileges(uid: int, gid: int, groups: list[int]):
    def drop() -> None:
        os.setgroups(groups)
        os.setgid(gid)
        os.setuid(uid)
    return drop


def run_as_service(command: list[str], state: dict, *, timeout: int = 300) -> subprocess.CompletedProcess[str]:
    uid, gid, groups = service_identity_from_state(state)
    source_environment = BOOTSTRAP_ENVIRONMENT if BOOTSTRAP_ENVIRONMENT is not None else os.environ
    environment = {key: value for key, value in source_environment.items() if key not in SYSTEMD_ENV_KEYS_TO_DROP}
    if not environment.get("HOME"):
        environment["HOME"] = pwd.getpwuid(uid).pw_dir
    try:
        return subprocess.run(
            command,
            cwd=state["installDir"] if pathlib.Path(state["installDir"]).is_dir() else target_console(state).parent,
            env=environment,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=timeout,
            check=False,
            preexec_fn=_drop_privileges(uid, gid, groups) if os.geteuid() == 0 else None,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise HandoffError(f"The migration utility {pathlib.Path(command[0]).name} could not complete.") from exc


def target_console(state: dict) -> pathlib.Path:
    installed = pathlib.Path(state["installDir"]) / "Chaptarr"
    if installed.is_file():
        return installed
    fallback = pathlib.Path(state["backupDir"]) / "target-app" / "Chaptarr"
    if fallback.is_file():
        return fallback
    raise HandoffError("The retained ChaptarrNG migration utility is missing.")


def console_action(state: dict, action: str, *extra: str, timeout: int = 300) -> subprocess.CompletedProcess[str]:
    command = [str(target_console(state)), action, *extra, f"-data={state['dataDir']}"]
    return run_as_service(command, state, timeout=timeout)


def check_migrated(state: dict) -> bool:
    return console_action(state, "-check-bookshelf-migration", timeout=HOOK_TIMEOUT).returncode == 0


def path_for_dropin(service: str) -> pathlib.Path:
    return pathlib.Path(SYSTEMD_PREFIX) / f"{service}.d" / SYSTEMD_DROPIN_NAME


def write_json_atomic(path: pathlib.Path, value: dict) -> None:
    temporary = path.with_name(path.name + f".tmp.{os.getpid()}")
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as output:
            json.dump(value, output, sort_keys=True, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    except BaseException:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass
        raise


def read_state(state_path: pathlib.Path) -> dict:
    if state_path.is_symlink() or not state_path.is_file():
        raise HandoffError("The handoff state file is missing or unsafe.")
    try:
        state = json.loads(state_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise HandoffError("The handoff state file is invalid.") from exc
    if state.get("schemaVersion") != SCHEMA_VERSION:
        raise HandoffError("The handoff state file uses an unsupported schema version.")
    return state


def write_state(state: dict) -> None:
    state["updatedAtUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    write_json_atomic(pathlib.Path(state["stateFile"]), state)


def write_dropin(state: dict) -> None:
    helper = pathlib.Path(state["helperPath"])
    state_file = pathlib.Path(state["stateFile"])
    if any(any(character.isspace() for character in str(value)) for value in (helper, state_file, sys.executable)):
        raise HandoffError("The bootstrap helper, state, or interpreter path cannot contain whitespace because systemd executes the recovery hook.")
    dropin = path_for_dropin(state["service"])
    dropin.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
    contents = (
        "[Service]\n"
        f"ExecStartPre=+{sys.executable} {helper} --hook pre-start --state-file {state_file}\n"
        f"ExecStartPost=+{sys.executable} {helper} --hook post-start --state-file {state_file}\n"
        f"ExecStopPost=+{sys.executable} {helper} --hook post-stop --state-file {state_file}\n"
        f"Environment=CHAPTARR_DEFAULT_PORT={state['port']}\n"
        # systemctl's start timeout includes both the bounded app startup and
        # the ExecStartPost health helper. Leave a short scheduling margin
        # beyond the configured health window so a completed migration can be
        # confirmed instead of being killed at the exact timeout boundary.
        f"TimeoutStartSec={int(state['healthTimeoutSeconds']) + SYSTEMD_STARTUP_MARGIN_SECONDS}s\n"
        f"TimeoutStopSec={SYSTEMD_STOP_TIMEOUT_SECONDS}s\n"
        "Restart=on-failure\n"
        "RestartSec=5s\n"
    )
    state["dropinSha256"] = hashlib.sha256(contents.encode("utf-8")).hexdigest()
    write_state(state)
    temporary = dropin.with_name(dropin.name + f".tmp.{os.getpid()}")
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    descriptor = os.open(temporary, flags, 0o644)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            output.write(contents)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, dropin)
    except BaseException:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass
        raise
    result = run_command(["systemctl", "daemon-reload"])
    if result.returncode != 0:
        raise HandoffError("systemd could not load the temporary migration recovery hooks.")


def remove_dropin(state: dict) -> None:
    dropin = path_for_dropin(state["service"])
    if dropin.is_file() and not dropin.is_symlink():
        digest = hashlib.sha256(dropin.read_bytes()).hexdigest()
        if digest != state.get("dropinSha256"):
            log(f"Preserving modified systemd recovery drop-in at {dropin}.")
            return
        dropin.unlink()
        try:
            dropin.parent.rmdir()
        except OSError:
            pass
        run_command(["systemctl", "daemon-reload"], quiet=True)


def write_runtime_mask(state: dict) -> None:
    result = run_command(["systemctl", "mask", "--runtime", state["service"]])
    if result.returncode != 0:
        raise HandoffError("systemd could not runtime-mask the service after automatic rollback failed.")
    result = run_command(["systemctl", "daemon-reload"])
    if result.returncode != 0:
        raise HandoffError("systemd could not reload the runtime service mask after automatic rollback failed.")


def copytree_preserving_ownership(source: pathlib.Path, destination: pathlib.Path) -> None:
    shutil.copytree(source, destination, symlinks=True, copy_function=shutil.copy2)
    for root, directories, files in os.walk(source, topdown=True, followlinks=False):
        source_root = pathlib.Path(root)
        relative_root = source_root.relative_to(source)
        destination_root = destination / relative_root
        try:
            source_stat = source_root.lstat()
            os.chown(destination_root, source_stat.st_uid, source_stat.st_gid)
        except OSError:
            pass
        for name in directories + files:
            source_path = source_root / name
            destination_path = destination_root / name
            source_stat = source_path.lstat()
            try:
                os.chown(destination_path, source_stat.st_uid, source_stat.st_gid, follow_symlinks=False)
            except (OSError, NotImplementedError):
                if not source_path.is_symlink():
                    raise


def restore_config(state: dict) -> None:
    config = pathlib.Path(state["dataDir"]) / "config.xml"
    if config.is_symlink():
        raise HandoffError("config.xml became a symlink; automatic rollback stopped.")
    saved = pathlib.Path(state["configSnapshot"])
    if state["configExisted"]:
        if saved.is_symlink() or not saved.is_file():
            raise HandoffError("The config.xml snapshot is missing or unsafe.")
        shutil.copy2(saved, config)
        os.chown(config, int(state["configUid"]), int(state["configGid"]))
        os.chmod(config, int(state["configMode"]))
    else:
        try:
            config.unlink()
        except FileNotFoundError:
            pass


def restore_application(state: dict) -> None:
    install_dir = pathlib.Path(state["installDir"])
    source_app = pathlib.Path(state["previousApp"])
    if source_app.is_symlink() or not source_app.is_dir():
        raise HandoffError("The previous application snapshot is missing or unsafe.")
    if install_dir.is_symlink():
        raise HandoffError("The current application path became a symlink; automatic rollback stopped.")
    backup_dir = pathlib.Path(state["backupDir"])
    failed_app = pathlib.Path(tempfile.mkdtemp(
        prefix="failed-target-app-",
        dir=backup_dir,
    ))
    failed_app.rmdir()
    if install_dir.exists():
        os.replace(install_dir, failed_app)
    copytree_preserving_ownership(source_app, install_dir)
    os.chmod(install_dir, int(state["installMode"]))
    os.chown(install_dir, int(state["installUid"]), int(state["installGid"]))


def restore_postgres_side_schemas(state: dict) -> None:
    source_backup = pathlib.Path(state["postgresBackupDir"])
    dumps = [
        source_backup / name
        for name in ("postgres-log.dump", "postgres-cache.dump")
        if (source_backup / name).is_file() and not (source_backup / name).is_symlink()
    ] if source_backup.is_dir() and not source_backup.is_symlink() else []
    if not dumps:
        return
    restore = console_action(
        state,
        "-restore-bookshelf-postgres",
        "-confirm-bookshelf-service-stopped",
        "-bookshelf-postgres-log-cache-only",
        f"-bookshelf-postgres-backup={source_backup}",
        timeout=MAX_HEALTH_TIMEOUT,
    )
    if restore.returncode != 0:
        raise HandoffError("The retained BookshelfNG PostgreSQL log/cache schemas could not be restored.")


def preserve_postgres_side_schemas(state: dict) -> None:
    source_backup = pathlib.Path(state["postgresBackupDir"])
    dumps = [
        source_backup / name
        for name in ("postgres-log.dump", "postgres-cache.dump")
        if (source_backup / name).is_file() and not (source_backup / name).is_symlink()
    ] if source_backup.is_dir() and not source_backup.is_symlink() else []
    if not dumps:
        return
    uid, gid, _ = service_identity_from_state(state)
    current_backup = pathlib.Path(tempfile.mkdtemp(
        prefix="target-postgres-log-cache-",
        dir=state["backupDir"],
    ))
    os.chmod(current_backup, 0o700)
    os.chown(current_backup, uid, gid)
    state["targetPostgresBackupDir"] = str(current_backup)
    write_state(state)
    backup = console_action(
        state,
        "-backup-bookshelf-postgres",
        "-confirm-bookshelf-service-stopped",
        "-bookshelf-postgres-log-cache-only",
        f"-bookshelf-postgres-backup={current_backup}",
        timeout=MAX_HEALTH_TIMEOUT,
    )
    if backup.returncode != 0:
        raise HandoffError("The current PostgreSQL log/cache schemas could not be backed up before rollback.")
    if not any(
        (current_backup / name).is_file() and not (current_backup / name).is_symlink()
        for name in ("postgres-log.dump", "postgres-cache.dump")
    ):
        current_backup.rmdir()
        state["targetPostgresBackupDir"] = None
        write_state(state)


def preview_postgres_side_schemas(state: dict) -> None:
    source_backup = pathlib.Path(state["postgresBackupDir"])
    dumps = [
        source_backup / name
        for name in ("postgres-log.dump", "postgres-cache.dump")
        if (source_backup / name).is_file() and not (source_backup / name).is_symlink()
    ] if source_backup.is_dir() and not source_backup.is_symlink() else []
    if not dumps:
        return
    preview = console_action(
        state,
        "-preview-bookshelf-postgres-restore",
        "-confirm-bookshelf-service-stopped",
        "-bookshelf-postgres-log-cache-only",
        f"-bookshelf-postgres-backup={source_backup}",
        timeout=MAX_HEALTH_TIMEOUT,
    )
    if preview.returncode != 0:
        raise HandoffError("The retained PostgreSQL log/cache backups could not be validated before database rollback.")


def rollback_handoff(state: dict, reason: str) -> bool:
    if state.get("status") not in ("pending", "rollback-requested"):
        return state.get("status") == "rolled-back"
    log(f"BookshelfNG first-start health did not pass ({reason}); restoring the retained source state.")
    try:
        if service_active(state["service"]) or service_main_pid(state["service"]) != 0:
            raise HandoffError("The service is still running; database rollback was not started.")
        if check_migrated(state):
            preview_postgres_side_schemas(state)
            preserve_postgres_side_schemas(state)
            result = console_action(
                state,
                "-restore-bookshelf",
                "-confirm-bookshelf-service-stopped",
                "-bookshelf-backup=auto",
                timeout=MAX_HEALTH_TIMEOUT,
            )
            if result.returncode != 0:
                raise HandoffError("The retained BookshelfNG database backup could not be restored.")
            restore_postgres_side_schemas(state)
        else:
            preview = run_as_service(
                [str(target_console(state)), *migration_preview_arguments(state)],
                state,
                timeout=MAX_HEALTH_TIMEOUT,
            )
            if preview.returncode != 0:
                native_path = pathlib.Path(state["dataDir"])
                if state.get("mergeSqliteSources") and not sqlite_database_has_data(native_path, "chaptarr.db"):
                    log("The dual BookshelfNG sources remain intact; the failed merge never promoted a ChaptarrNG target.")
                else:
                    preview_postgres_side_schemas(state)
                    preserve_postgres_side_schemas(state)
                    result = console_action(
                        state,
                        "-restore-bookshelf",
                        "-confirm-bookshelf-service-stopped",
                        "-bookshelf-backup=auto",
                        timeout=MAX_HEALTH_TIMEOUT,
                    )
                    if result.returncode != 0:
                        raise HandoffError("The source database is not readable and the retained BookshelfNG backup could not be restored.")
                    restore_postgres_side_schemas(state)
        restore_config(state)
        restore_application(state)
        temporary_original_value = state.get("temporaryOriginalPath")
        if temporary_original_value:
            temporary_original = pathlib.Path(temporary_original_value)
            expected_prefix = f".{pathlib.Path(state['installDir']).name}.bookshelfng-original-"
            if temporary_original.parent == pathlib.Path(state["installDir"]).parent and temporary_original.name.startswith(expected_prefix):
                if temporary_original.is_dir() and not temporary_original.is_symlink():
                    try:
                        shutil.rmtree(temporary_original)
                    except OSError as exc:
                        log(f"The source application was restored, but its extra temporary copy remains at {temporary_original}: {exc}")
        state["status"] = "rolled-back"
        state["outcome"] = "source database, config.xml, and prior application restored"
        write_state(state)
        return True
    except (HandoffError, OSError) as exc:
        state["status"] = "intervention-required"
        state["outcome"] = "automatic rollback could not be verified"
        state["failure"] = str(exc)
        try:
            write_state(state)
        except OSError as state_error:
            log(f"Could not persist the intervention state: {state_error}")
        write_runtime_mask(state)
        log(f"Automatic rollback stopped: {exc}")
        return False


def health_url(state: dict) -> str:
    url_base = state["urlBase"].strip("/")
    path = (f"/{url_base}" if url_base else "") + "/ping"
    host = state.get("healthHost", "127.0.0.1")
    if ":" in host and not host.startswith("["):
        host = f"[{host}]"
    return f"http://{host}:{int(state['port'])}{path}"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        return None


def http_healthy(state: dict) -> bool:
    request = urllib.request.Request(health_url(state), headers={"User-Agent": "ChaptarrNG-Bookshelf-Handoff/1"})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        with opener.open(request, timeout=3) as response:
            return 200 <= response.status < 300
    except (OSError, urllib.error.URLError, urllib.error.HTTPError, http.client.HTTPException):
        return False


def post_start_hook(state: dict) -> int:
    if state.get("status") != "pending":
        return 0
    deadline = time.monotonic() + int(state["healthTimeoutSeconds"])
    next_database_check = 0.0
    while time.monotonic() < deadline:
        if not service_starting_or_active(state["service"]) or service_main_pid(state["service"]) == 0:
            state["status"] = "rollback-requested"
            state["failure"] = "the app stopped before health was confirmed"
            write_state(state)
            return 1
        if http_healthy(state) and time.monotonic() >= next_database_check:
            if check_migrated(state):
                state["status"] = "healthy"
                state["outcome"] = "local health and migrated database checks passed"
                write_state(state)
                log("BookshelfNG migration and ChaptarrNG startup health passed.")
                return 0
            next_database_check = time.monotonic() + 10
        time.sleep(2)
    state["status"] = "rollback-requested"
    state["failure"] = f"health was not confirmed within {state['healthTimeoutSeconds']} seconds"
    write_state(state)
    return 1


def pre_start_hook(state: dict) -> int:
    status = state.get("status")
    if status in ("healthy", "rolled-back", "cancelled"):
        remove_dropin(state)
        return 0
    if status == "intervention-required":
        log("The BookshelfNG handoff requires operator recovery; refusing to start either application automatically.")
        return 1
    if status == "rollback-requested":
        if not rollback_handoff(state, state.get("failure", "operator requested recovery before startup")):
            return 1
        remove_dropin(state)
        return 0
    if status not in ("prepared", "pending"):
        log(f"The BookshelfNG handoff has an unknown recovery state ({status!r}); refusing to start the service.")
        return 1
    install_dir = pathlib.Path(state["installDir"])
    legacy_executable = pathlib.Path(state["legacyExecutable"])
    target = install_dir / "Chaptarr"
    if target.is_file() and legacy_executable.is_symlink() and legacy_executable.resolve() == target.resolve():
        return 0
    if status == "prepared":
        state["status"] = "cancelled"
        state["outcome"] = "bootstrap stopped before replacing the existing application"
        write_state(state)
        remove_dropin(state)
        return 0
    if not install_dir.exists() or not target.is_file():
        try:
            restore_application(state)
            restore_config(state)
            state["status"] = "rolled-back"
            state["outcome"] = "interrupted file promotion recovered before app startup"
            write_state(state)
            remove_dropin(state)
            return 0
        except HandoffError as exc:
            state["status"] = "intervention-required"
            state["failure"] = str(exc)
            write_state(state)
            write_runtime_mask(state)
            log(f"Interrupted BookshelfNG promotion requires operator recovery: {exc}")
            return 1
    if state.get("status") == "pending":
        if not rollback_handoff(state, "the legacy executable link or target application is incomplete"):
            return 1
        remove_dropin(state)
    return 0


def recover_mode(args: argparse.Namespace) -> int:
    if os.geteuid() != 0:
        raise HandoffError("Run recovery as root (usually with sudo).")
    state_path = pathlib.Path(args.state_file)
    state = read_state(state_path)
    if state.get("status") != "intervention-required":
        raise HandoffError("--recover is only available when the retained handoff state says intervention-required.")
    service = validate_service_name(state.get("service", ""))
    lock_descriptor = acquire_service_handoff_lock(service)
    try:
        return recover_mode_locked(args, state_path, service)
    finally:
        os.close(lock_descriptor)


def recover_mode_locked(args: argparse.Namespace, state_path: pathlib.Path, service: str) -> int:
    state = read_state(state_path)
    if state.get("status") != "intervention-required":
        raise HandoffError("--recover is only available when the retained handoff state says intervention-required.")
    if validate_service_name(state.get("service", "")) != service:
        raise HandoffError("The handoff service identity changed while recovery was waiting for its lock.")
    if service_active(service):
        result = run_command(["systemctl", "stop", service], timeout=120)
        if result.returncode != 0:
            raise HandoffError("systemd could not stop the service before recovery.")
    if service_active(service) or service_main_pid(service) != 0:
        raise HandoffError("The service is still running; recovery did not touch its database.")

    state["status"] = "rollback-requested"
    state["failure"] = "operator requested a retry of the retained automatic rollback"
    write_state(state)
    unmask = run_command(["systemctl", "unmask", "--runtime", service])
    if unmask.returncode != 0:
        raise HandoffError("systemd could not remove the runtime mask before recovery.")
    reload_result = run_command(["systemctl", "daemon-reload"])
    if reload_result.returncode != 0:
        raise HandoffError("systemd could not reload the unit before recovery.")
    run_command(["systemctl", "reset-failed", service], quiet=True)
    start = run_command(["systemctl", "start", service], timeout=int(state["healthTimeoutSeconds"]) + 180, quiet=True)
    recovered = read_state(state_path)
    if start.returncode != 0 or recovered.get("status") != "rolled-back":
        raise HandoffError("Recovery did not confirm a restored BookshelfNG database and application; the service remains stopped for review.")
    log(f"BookshelfNG recovery completed. The restored service state and backups remain at {state['backupDir']}.")
    return 0


def post_stop_hook(state: dict) -> int:
    if state.get("status") not in ("pending", "rollback-requested"):
        return 0
    return 0 if rollback_handoff(state, state.get("failure", "the service stopped during first-start migration")) else 1


def ensure_space(path: pathlib.Path, required_bytes: int, label: str) -> None:
    usage = shutil.disk_usage(path)
    if usage.free < required_bytes:
        raise HandoffError(f"There is not enough free space on the {label} filesystem for the rollback copy and staged application.")


def confirm_handoff(migration_mode: str, *, input_stream=None, output_stream=None) -> bool:
    if migration_mode == "automatic":
        return True
    if migration_mode != "ask":
        raise HandoffError("The migration mode must be 'ask' or 'automatic'.")

    input_stream = sys.stdin if input_stream is None else input_stream
    output_stream = sys.stderr if output_stream is None else output_stream
    if not input_stream.isatty() or not output_stream.isatty():
        raise HandoffError("No interactive terminal is available; migration was not started. Use --migration-mode automatic only when an unattended handoff is intended.")

    print(
        "Replace BookshelfNG with ChaptarrNG now? No leaves the BookshelfNG installation and its data unchanged. [y/N] ",
        end="",
        file=output_stream,
        flush=True,
    )
    response = input_stream.readline().strip().lower()
    if response in ("y", "yes"):
        return True
    if response in ("", "n", "no"):
        return False
    raise HandoffError("Please answer y or n; no changes were made.")


def install_mode(args: argparse.Namespace) -> int:
    if os.geteuid() != 0:
        raise HandoffError("Run the one-time systemd bootstrap as root (usually with sudo).")
    service = validate_service_name(args.service)
    if not confirm_handoff(args.migration_mode):
        log("No changes made; the BookshelfNG installation and data remain unchanged.")
        return 0

    lock_descriptor = acquire_service_handoff_lock(service)
    try:
        return install_mode_locked(args, service)
    finally:
        os.close(lock_descriptor)


def install_mode_locked(args: argparse.Namespace, service: str) -> int:
    install_dir = absolute_directory(args.install_dir, "--install-dir", must_exist=True)
    data_dir = absolute_directory(args.data_dir, "--data-dir", must_exist=True)
    archive_argument = pathlib.Path(args.archive)
    if archive_argument.is_symlink():
        raise HandoffError("--archive must not be a symlink.")
    archive = archive_argument.resolve(strict=True)
    if not archive.is_file():
        raise HandoffError("--archive must name a regular release archive.")
    backup_dir = absolute_directory(args.backup_dir, "--backup-dir", must_exist=False)
    require_private_root_parent(backup_dir)
    if any(part.isspace() for part in str(backup_dir)):
        raise HandoffError("--backup-dir cannot contain whitespace because systemd invokes the retained recovery helper.")
    for path, label in ((backup_dir, "--backup-dir"), (install_dir, "--install-dir"), (data_dir, "--data-dir")):
        if path == backup_dir:
            continue
        if backup_dir == path or path in backup_dir.parents or backup_dir in path.parents:
            raise HandoffError(f"--backup-dir must be separate from {label}.")
    if install_dir == data_dir or install_dir in data_dir.parents or data_dir in install_dir.parents:
        raise HandoffError("--install-dir and --data-dir must be separate paths so replacing the application cannot move or overwrite appdata.")
    legacy_executable = pathlib.Path(args.legacy_executable)
    if not legacy_executable.is_absolute() or any(part == ".." for part in legacy_executable.parts):
        raise HandoffError("--legacy-executable must be an absolute path without '..'.")
    legacy_parent = legacy_executable.parent.resolve(strict=True)
    try:
        relative_executable = legacy_parent.relative_to(install_dir)
    except ValueError as exc:
        raise HandoffError("--legacy-executable must be inside --install-dir.") from exc
    legacy_executable = install_dir / relative_executable / legacy_executable.name
    if legacy_executable.is_symlink() or not legacy_executable.is_file() or not os.access(legacy_executable, os.X_OK):
        raise HandoffError("--legacy-executable must be an executable regular file inside --install-dir.")
    if os.path.ismount(install_dir):
        raise HandoffError("--install-dir is a mount point and cannot be atomically replaced.")
    if path_for_dropin(service).exists() or path_for_dropin(service).is_symlink():
        raise HandoffError("The bootstrap systemd drop-in already exists; inspect it before retrying.")
    if not service_active(service):
        raise HandoffError("Start the existing BookshelfNG systemd service before running the bootstrap; it reads the service's effective environment without copying credentials to disk.")
    main_pid = service_main_pid(service)
    if main_pid <= 0:
        raise HandoffError("The BookshelfNG service has no running main process.")
    environment, argv, process_executable = read_process(main_pid)
    if process_executable.resolve() != legacy_executable.resolve():
        argv_executable = pathlib.Path(argv[0])
        if not argv_executable.is_absolute() or argv_executable.resolve() != legacy_executable.resolve():
            raise HandoffError("--legacy-executable does not match the running BookshelfNG service process.")
    validate_service_data_directory(data_dir, argv)
    dynamic_user, service_gid, groups, username, group_name = resolve_service_identity(service)
    port, url_base, health_host = app_settings(data_dir, environment, argv)
    source_is_postgres = postgres_source(data_dir, environment)
    merge_sqlite_sources = not source_is_postgres and has_dual_populated_sqlite_sources(data_dir)
    version, archive_bytes, archive_sha256 = verify_release_archive(archive)
    archive_size = archive.stat().st_size
    service_copy_bytes = sum(path.stat().st_size for path in install_dir.rglob("*") if path.is_file() and not path.is_symlink())
    ensure_space(backup_dir.parent, service_copy_bytes + archive_bytes + archive_size, "backup")
    ensure_space(install_dir.parent, archive_bytes + archive_size, "application")

    backup_dir.mkdir(mode=0o700, parents=True)
    archive_snapshot = backup_dir / archive.name
    sidecar = pathlib.Path(str(archive) + ".sha256")
    sidecar_snapshot = pathlib.Path(str(archive_snapshot) + ".sha256")
    copy_private_regular_file(archive, archive_snapshot)
    copy_private_regular_file(sidecar, sidecar_snapshot)
    snapshot_identity = verify_release_archive(archive_snapshot)
    if snapshot_identity != (version, archive_bytes, archive_sha256):
        raise HandoffError("The release archive or checksum changed while the private handoff copy was being made.")
    ensure_space(backup_dir, service_copy_bytes + archive_bytes, "backup")
    ensure_space(install_dir.parent, archive_bytes, "application")
    helper_path = backup_dir / "bookshelfng-systemd-bootstrap.py"
    shutil.copy2(pathlib.Path(__file__).resolve(), helper_path)
    os.chmod(helper_path, 0o755)
    state_path = backup_dir / "state.json"
    previous_app = backup_dir / "previous-app"
    config_snapshot = backup_dir / "config.xml"
    postgres_backup_dir = backup_dir / "postgres-source"
    config_file = data_dir / "config.xml"
    if config_file.is_symlink():
        raise HandoffError("config.xml is a symlink; the bootstrap will not replace it automatically.")
    config_existed = config_file.is_file()
    if config_file.exists() and not config_existed:
        raise HandoffError("config.xml exists but is not a regular file; the bootstrap will not replace it automatically.")
    data_stat = data_dir.stat()
    install_stat = install_dir.stat()
    state = {
        "schemaVersion": SCHEMA_VERSION,
        "status": "prepared",
        "service": service,
        "installDir": str(install_dir),
        "legacyExecutable": str(legacy_executable),
        "dataDir": str(data_dir),
        "backupDir": str(backup_dir),
        "previousApp": str(previous_app),
        "targetApp": str(backup_dir / "target-app"),
        "configSnapshot": str(config_snapshot),
        "configExisted": config_existed,
        "dataUid": data_stat.st_uid,
        "dataGid": data_stat.st_gid,
        "installUid": install_stat.st_uid,
        "installGid": install_stat.st_gid,
        "installMode": stat.S_IMODE(install_stat.st_mode),
        "uid": dynamic_user,
        "gid": service_gid,
        "groups": groups,
        "serviceUser": username,
        "serviceGroup": group_name,
        "port": port,
        "urlBase": url_base,
        "healthHost": health_host,
        "healthTimeoutSeconds": args.health_timeout_seconds,
        "postgresSource": source_is_postgres,
        "mergeSqliteSources": merge_sqlite_sources,
        "helperPath": str(helper_path),
        "stateFile": str(state_path),
        "postgresBackupDir": str(postgres_backup_dir),
        "releaseVersion": version,
        "releaseArchive": str(archive_snapshot),
        "releaseSha256": archive_sha256,
        "wasEnabled": systemctl_value("UnitFileState", service) in ("enabled", "enabled-runtime", "linked", "linked-runtime"),
        "originalServiceActive": True,
        "createdAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    global BOOTSTRAP_ENVIRONMENT
    BOOTSTRAP_ENVIRONMENT = environment
    stage_parent = pathlib.Path(tempfile.mkdtemp(prefix=".chaptarrng-bookshelf-bootstrap-", dir=install_dir.parent))
    os.chmod(stage_parent, 0o755)
    try:
        staged_app = extract_release_archive(archive_snapshot, stage_parent / "release")
        os.chown(staged_app, dynamic_user, service_gid)
        for path in staged_app.rglob("*"):
            if not path.is_symlink():
                os.chown(path, dynamic_user, service_gid)
        target_app = pathlib.Path(state["targetApp"])
        shutil.copytree(staged_app, target_app, symlinks=True, copy_function=shutil.copy2)
        os.chown(target_app, dynamic_user, service_gid)
        os.chmod(target_app, 0o755)
        for path in target_app.rglob("*"):
            if not path.is_symlink():
                os.chown(path, dynamic_user, service_gid)
        staged_console = staged_app / "Chaptarr"
        if not service_active(service):
            raise HandoffError("BookshelfNG stopped while the bootstrap was preparing; start it and retry.")
        stop_result = run_command(["systemctl", "stop", service], timeout=120)
        if stop_result.returncode != 0:
            raise HandoffError("systemd could not stop BookshelfNG cleanly.")
        if service_active(service) or service_main_pid(service) != 0:
            raise HandoffError("BookshelfNG is still running; no files or database were changed.")

        preview = run_as_service(
            [str(staged_console), *migration_preview_arguments(state)],
            state,
            timeout=MAX_HEALTH_TIMEOUT,
        )
        if preview.returncode != 0:
            raise HandoffError("The ChaptarrNG migration preview failed; BookshelfNG data was left unchanged.")

        if config_existed:
            shutil.copy2(config_file, config_snapshot)
            os.chmod(config_snapshot, 0o600)
            config_stat = config_file.stat()
            state["configUid"] = config_stat.st_uid
            state["configGid"] = config_stat.st_gid
            state["configMode"] = stat.S_IMODE(config_stat.st_mode)
        else:
            state["configUid"] = data_stat.st_uid
            state["configGid"] = data_stat.st_gid
            state["configMode"] = 0o640
        os.chmod(backup_dir, 0o711)
        copytree_preserving_ownership(install_dir, previous_app)
        os.chmod(previous_app, 0o700)

        if source_is_postgres:
            postgres_backup_dir.mkdir(mode=0o700)
            os.chown(postgres_backup_dir, dynamic_user, service_gid)
            preflight_result = run_as_service(
                [
                    str(staged_console),
                    "-check-bookshelf-postgres-backup",
                    "-confirm-bookshelf-service-stopped",
                    f"-data={data_dir}",
                ],
                state,
                timeout=600,
            )
            if preflight_result.returncode != 0:
                raise HandoffError("BookshelfNG PostgreSQL backup preflight failed; migration was not started.")
            backup_result = run_as_service(
                [
                    str(staged_console),
                    "-backup-bookshelf-postgres",
                    "-confirm-bookshelf-service-stopped",
                    f"-bookshelf-postgres-backup={postgres_backup_dir}",
                    f"-data={data_dir}",
                ],
                state,
                timeout=MAX_HEALTH_TIMEOUT,
            )
            if backup_result.returncode != 0:
                raise HandoffError("BookshelfNG PostgreSQL could not be independently backed up; migration was not started.")
            if not list(postgres_backup_dir.glob("postgres-*.dump")):
                raise HandoffError("PostgreSQL backup completed without producing a verified dump; migration was not started.")

        hold_path = install_dir.with_name(f".{install_dir.name}.bookshelfng-original-{os.getpid()}")
        if hold_path.exists():
            raise HandoffError("A previous application handoff directory already exists; refusing to overwrite it.")
        state["status"] = "pending"
        state["temporaryOriginalPath"] = str(hold_path)
        write_state(state)
        write_dropin(state)
        persist_target_server_settings(data_dir, port, argv, state)
        os.replace(install_dir, hold_path)
        try:
            os.replace(staged_app, install_dir)
        except OSError:
            os.replace(hold_path, install_dir)
            raise
        link_path = install_dir / relative_executable / legacy_executable.name
        link_path.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
        if link_path.exists() or link_path.is_symlink():
            link_path.unlink()
        if link_path == install_dir / "Chaptarr":
            raise HandoffError("The legacy executable path collides with the new Chaptarr executable.")
        os.symlink(os.path.relpath(install_dir / "Chaptarr", link_path.parent), link_path)
        os.chown(install_dir, dynamic_user, service_gid)
        for path in install_dir.rglob("*"):
            if not path.is_symlink():
                os.chown(path, dynamic_user, service_gid)
        os.chmod(install_dir, int(state["installMode"]))
        start_result = run_command(
            ["systemctl", "start", service],
            timeout=args.health_timeout_seconds + SYSTEMD_STARTUP_MARGIN_SECONDS,
            quiet=True,
        )
        state = read_state(state_path)
        if start_result.returncode == 0 and state.get("status") == "healthy":
            try:
                shutil.rmtree(hold_path)
            except OSError:
                pass
            remove_dropin(state)
            log(f"BookshelfNG was migrated to ChaptarrNG {version}; the verified rollback copy is retained at {backup_dir}.")
            return 0
        if state.get("status") == "rolled-back":
            run_command(["systemctl", "reset-failed", service], quiet=True)
            old_start = run_command(["systemctl", "start", service], timeout=120, quiet=True)
            if old_start.returncode != 0:
                log("BookshelfNG data and application were restored, but the previous service did not restart; inspect systemd logs.")
            else:
                log("The first ChaptarrNG startup failed. BookshelfNG data and application were restored and the old service is running.")
            remove_dropin(state)
            return 1
        if state.get("status") == "intervention-required":
            raise HandoffError("Automatic rollback needs operator review; the service is runtime-masked and all snapshots are retained.")
        raise HandoffError("The systemd first-start supervisor did not record a healthy or rolled-back result.")
    except BaseException:
        current_state = None
        if state_path.exists():
            try:
                current_state = read_state(state_path)
            except HandoffError:
                pass
        if current_state and current_state.get("status") in ("pending", "rollback-requested"):
            if service_active(service):
                run_command(["systemctl", "stop", service], timeout=120, quiet=True)
            if rollback_handoff(current_state, "the bootstrap process was interrupted"):
                run_command(["systemctl", "reset-failed", service], quiet=True)
                run_command(["systemctl", "start", service], timeout=120, quiet=True)
                remove_dropin(current_state)
        elif current_state and current_state.get("status") == "rolled-back":
            run_command(["systemctl", "reset-failed", service], quiet=True)
            run_command(["systemctl", "start", service], timeout=120, quiet=True)
            remove_dropin(current_state)
        elif current_state and current_state.get("status") == "intervention-required":
            pass
        elif not service_active(service) and state.get("originalServiceActive"):
            run_command(["systemctl", "start", service], timeout=120, quiet=True)
        raise
    finally:
        try:
            shutil.rmtree(stage_parent)
        except OSError:
            pass


def hook_mode(args: argparse.Namespace) -> int:
    state = read_state(pathlib.Path(args.state_file))
    if state.get("service") != os.environ.get("SYSTEMD_UNIT", state.get("service")):
        raise HandoffError("The recovery hook does not match the recorded service.")
    if args.hook == "pre-start":
        return pre_start_hook(state)
    if args.hook == "post-start":
        return post_start_hook(state)
    if args.hook == "post-stop":
        return post_stop_hook(state)
    raise HandoffError("Unknown systemd recovery hook.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--hook", choices=("pre-start", "post-start", "post-stop"), help=argparse.SUPPRESS)
    parser.add_argument("--state-file", help=argparse.SUPPRESS)
    parser.add_argument("--recover", action="store_true", help="Retry a retained systemd handoff rollback")
    parser.add_argument("--service", help="Existing active BookshelfNG systemd unit, including .service")
    parser.add_argument("--install-dir", help="Existing BookshelfNG application directory")
    parser.add_argument("--legacy-executable", help="The exact executable path used by the running service")
    parser.add_argument("--data-dir", help="BookshelfNG appdata directory used by the service")
    parser.add_argument("--archive", help="Checksum-verified ChaptarrNG linux-x64 or linux-arm64 release archive")
    parser.add_argument("--backup-dir", help="New absolute path outside appdata and the install directory for retained rollback files")
    parser.add_argument(
        "--migration-mode",
        choices=("ask", "automatic"),
        default="ask",
        help="Ask before replacing BookshelfNG (default), or explicitly skip the prompt for an unattended handoff",
    )
    parser.add_argument("--health-timeout-seconds", type=int, default=MAX_HEALTH_TIMEOUT)
    args = parser.parse_args()
    if args.hook:
        if not args.state_file:
            parser.error("--state-file is required with --hook")
        return args
    if args.recover:
        if not args.state_file:
            parser.error("--state-file is required with --recover")
        return args
    required = ("service", "install_dir", "legacy_executable", "data_dir", "archive", "backup_dir")
    missing = [name.replace("_", "-") for name in required if not getattr(args, name)]
    if missing:
        parser.error("missing required arguments: " + ", ".join("--" + name for name in missing))
    if args.health_timeout_seconds < 1 or args.health_timeout_seconds > MAX_HEALTH_TIMEOUT:
        parser.error(f"--health-timeout-seconds must be between 1 and {MAX_HEALTH_TIMEOUT}")
    return args


def main() -> int:
    try:
        args = parse_args()
        if args.hook:
            return hook_mode(args)
        if args.recover:
            return recover_mode(args)
        return install_mode(args)
    except (HandoffError, OSError, tarfile.TarError) as exc:
        log(f"BookshelfNG bootstrap stopped: {exc}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
