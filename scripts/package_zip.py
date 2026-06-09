import os
import sys
import zipfile


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: package_zip.py <source_dir> <destination_zip>", file=sys.stderr)
        return 2

    source_dir = os.path.abspath(sys.argv[1])
    destination_zip = os.path.abspath(sys.argv[2])

    if not os.path.isdir(source_dir):
        print(f"source directory does not exist: {source_dir}", file=sys.stderr)
        return 1

    os.makedirs(os.path.dirname(destination_zip), exist_ok=True)

    with zipfile.ZipFile(destination_zip, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for root, dirs, files in os.walk(source_dir):
            dirs.sort()
            for filename in sorted(files):
                full_path = os.path.join(root, filename)
                archive_name = os.path.relpath(full_path, source_dir).replace(os.sep, "/")
                archive.write(full_path, archive_name)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
