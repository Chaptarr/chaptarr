#!/usr/bin/env python3
import io
import json
import unittest
from unittest.mock import patch

from check_stable_release_version import (
    fetch_published_releases,
    latest_stable_release,
    parse_version,
    release_is_newer,
)


class StableReleaseVersionTests(unittest.TestCase):
    def test_uses_most_recently_published_stable_release(self):
        releases = [
            {"tag_name": "v0.10.0", "draft": False, "prerelease": False, "published_at": "2026-01-01T00:00:00Z"},
            {"tag_name": "v0.11.0", "draft": True, "prerelease": False, "published_at": "2026-03-01T00:00:00Z"},
            {"tag_name": "v0.12.0-rc.1", "draft": False, "prerelease": True, "published_at": "2026-04-01T00:00:00Z"},
            {"tag_name": "nightly", "draft": False, "prerelease": False, "published_at": "2026-05-01T00:00:00Z"},
            {"tag_name": "v0.9.9", "draft": False, "prerelease": False, "published_at": "2026-02-01T00:00:00Z"},
        ]

        self.assertEqual(latest_stable_release(releases)["tag_name"], "v0.9.9")

    def test_compares_numeric_semver_components(self):
        self.assertTrue(release_is_newer("0.10.0", "v0.9.999"))
        self.assertFalse(release_is_newer("0.9.10", "v0.10.0"))
        self.assertFalse(release_is_newer("0.10.0", "v0.10.0"))

    def test_missing_stable_release_allows_first_release(self):
        self.assertTrue(release_is_newer("0.1.0", None))
        self.assertIsNone(latest_stable_release([]))

    def test_rejects_non_stable_package_versions(self):
        for version in ("0.9", "01.2.3", "1.2.3-rc.1", "v1.2.3"):
            with self.subTest(version=version):
                with self.assertRaises(ValueError):
                    parse_version(version)

    def test_rejects_invalid_release_tags_for_comparison(self):
        with self.assertRaises(ValueError):
            release_is_newer("1.2.4", "nightly")

    @patch("check_stable_release_version.urllib.request.build_opener")
    def test_fetch_uses_authenticated_github_releases_api(self, build_opener):
        opener = build_opener.return_value
        opener.open.return_value = io.BytesIO(b"[]")

        self.assertEqual(fetch_published_releases("owner/repo", "test-token"), [])

        request = opener.open.call_args.args[0]
        self.assertEqual(request.full_url, "https://api.github.com/repos/owner/repo/releases?per_page=100&page=1")
        self.assertEqual(request.get_header("Authorization"), "Bearer test-token")

    @patch("check_stable_release_version.urllib.request.build_opener")
    def test_fetches_all_release_pages(self, build_opener):
        opener = build_opener.return_value
        opener.open.side_effect = [
            io.BytesIO(json.dumps([{}] * 100).encode("utf-8")),
            io.BytesIO(b"[]"),
        ]

        self.assertEqual(len(fetch_published_releases("owner/repo", "test-token")), 100)

        urls = [call.args[0].full_url for call in opener.open.call_args_list]
        self.assertEqual(urls, [
            "https://api.github.com/repos/owner/repo/releases?per_page=100&page=1",
            "https://api.github.com/repos/owner/repo/releases?per_page=100&page=2",
        ])

    def test_rejects_invalid_repository_and_missing_token(self):
        with self.assertRaises(ValueError):
            fetch_published_releases("owner/repo/extra", "test-token")
        with self.assertRaises(ValueError):
            fetch_published_releases("owner/repo", "")


if __name__ == "__main__":
    unittest.main()
