"""Regression checks for vendor ZIP archives containing shared-library links."""
import importlib.util
import os
from pathlib import Path
import stat
import tempfile
import unittest
import zipfile

script = Path(__file__).resolve().parents[1] / "workers" / "native-libraries.py"
spec = importlib.util.spec_from_file_location("native_libraries", script)
libraries = importlib.util.module_from_spec(spec)
spec.loader.exec_module(libraries)


class NativeLibraryArchiveTests(unittest.TestCase):
    def test_regular_vendor_files_are_extracted(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "vendor.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("vendor/libexample.so.1", b"binary-library")
            libraries.copy_zip(archive, root)
            self.assertEqual((root / "libexample.so.1").read_bytes(), b"binary-library")

    def test_unsafe_library_links_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "vendor.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                link = zipfile.ZipInfo("vendor/libexample.so")
                link.create_system = 3
                link.external_attr = (stat.S_IFLNK | 0o777) << 16
                bundle.writestr(link, "../outside.so")
            with self.assertRaisesRegex(ValueError, "escapes"):
                libraries.copy_zip(archive, root)

    @unittest.skipIf(os.name == "nt", "UNIX vendor links are installed on POSIX")
    def test_versioned_shared_library_link_resolves_to_its_binary(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "vendor.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("vendor/libexample.so.1", b"binary-library")
                link = zipfile.ZipInfo("vendor/libexample.so")
                link.create_system = 3
                link.external_attr = (stat.S_IFLNK | 0o777) << 16
                bundle.writestr(link, "libexample.so.1")
            libraries.copy_zip(archive, root)
            self.assertTrue((root / "libexample.so").is_symlink())
            self.assertEqual((root / "libexample.so").read_bytes(), b"binary-library")


if __name__ == "__main__":
    unittest.main()
