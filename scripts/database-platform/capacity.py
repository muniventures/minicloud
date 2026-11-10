"""Fail before plaintext snapshots can exhaust tmpfs or available host memory."""
from pathlib import Path
import shutil


class BackupCapacityError(RuntimeError):
    pass


def available_memory():
    values = {}
    for line in Path('/proc/meminfo').read_text().splitlines():
        name, value = line.split(':', 1)
        if name == 'MemAvailable':
            values[name] = int(value.strip().split()[0]) * 1024
    available = values.get('MemAvailable', 0)
    limit = Path('/sys/fs/cgroup/memory.max')
    current = Path('/sys/fs/cgroup/memory.current')
    if limit.exists() and current.exists():
        text = limit.read_text().strip()
        if text != 'max':
            available = min(available, max(0, int(text) - int(current.read_text().strip())))
    return available


def require_capacity(required, *, directory='/dev/shm', disk_usage=shutil.disk_usage, memory=available_memory):
    if type(required) is not int or required < 0:
        raise ValueError('Invalid snapshot size')
    try:
        disk_free = disk_usage(directory).free
        memory_free = memory()
    except (OSError, ValueError):
        raise BackupCapacityError('Backup capacity observation unavailable') from None
    # tmpfs and RAM have separate reserves. A large nominal tmpfs cannot grant
    # permission to consume memory that the source application still needs.
    if disk_free < required + 8 * 1024**2 or memory_free < required + 64 * 1024**2:
        raise BackupCapacityError('Insufficient memory-backed backup workspace')


def require_disk_capacity(directory, required, *, disk_usage=shutil.disk_usage):
    if type(required) is not int or required < 0:
        raise ValueError('Invalid encrypted workspace size')
    try:
        disk = disk_usage(directory)
    except OSError:
        raise BackupCapacityError('Encrypted workspace capacity unavailable') from None
    if disk.free < required + max(256 * 1024**2, disk.total * 15 // 100):
        raise BackupCapacityError('Insufficient encrypted backup workspace')


def write_encrypted_stream(source, target, directory, maximum=8 * 1024**3):
    total = 0
    while data := source.read(1024**2):
        total += len(data)
        if total > maximum:
            raise BackupCapacityError('Encrypted backup exceeds supported size')
        require_disk_capacity(directory, len(data))
        target.write(data)
    return total
