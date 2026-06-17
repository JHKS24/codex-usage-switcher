"""Behavior tests for the pure logic in codex-desktop-switch.

The script has no .py extension, so it is loaded via importlib. Only
filesystem-free helpers are exercised here; the process/HTTP edges are
covered by the CI smoke tests.
"""
from __future__ import annotations

import importlib.util
import pathlib
import sys
from importlib.machinery import SourceFileLoader

import pytest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "codex-desktop-switch"
# No .py extension, so spec_from_file_location cannot infer a loader.
_loader = SourceFileLoader("codex_desktop_switch", str(SCRIPT))
_spec = importlib.util.spec_from_loader("codex_desktop_switch", _loader)
mod = importlib.util.module_from_spec(_spec)
sys.modules["codex_desktop_switch"] = mod
_loader.exec_module(mod)


class TestExitCodes:
    def test_codes_are_distinct(self):
        codes = [
            mod.EXIT_OK,
            mod.EXIT_CODEX_RUNNING,
            mod.EXIT_KEYRING,
            mod.EXIT_MISSING,
            mod.EXIT_PROCESS_UNKNOWN,
            mod.EXIT_CDX_USAGE,
            mod.EXIT_OTHER,
            mod.EXIT_CLAUDE_USAGE,
        ]
        assert len(set(codes)) == len(codes)

    def test_codes_avoid_traceback_and_argparse_values(self):
        for code in (
            mod.EXIT_CODEX_RUNNING,
            mod.EXIT_KEYRING,
            mod.EXIT_MISSING,
            mod.EXIT_PROCESS_UNKNOWN,
            mod.EXIT_CDX_USAGE,
            mod.EXIT_OTHER,
            mod.EXIT_CLAUDE_USAGE,
        ):
            assert code not in (1, 2)


class TestNormalizeUsageWindow:
    def test_normal_window(self):
        window = mod.normalize_usage_window({"used_percent": 30, "reset_after_seconds": 600})
        assert window["used_percent"] == 30.0
        assert window["remaining_percent"] == 70.0
        assert window["reset_after_seconds"] == 600
        assert window["reset_at"] is not None

    def test_clamps_out_of_range(self):
        assert mod.normalize_usage_window({"used_percent": 150})["remaining_percent"] == 0.0
        assert mod.normalize_usage_window({"used_percent": -5})["used_percent"] == 0.0

    def test_bad_input(self):
        assert mod.normalize_usage_window(None) is None
        assert mod.normalize_usage_window("nope") is None
        assert mod.normalize_usage_window({"used_percent": "garbage"})["used_percent"] == 0.0

    def test_camel_case_keys(self):
        window = mod.normalize_usage_window({"usedPercent": 12.5, "resetAfterSeconds": 60})
        assert window["used_percent"] == 12.5
        assert window["reset_after_seconds"] == 60


class TestClaudeFailureClassification:
    @pytest.mark.parametrize(
        ("detail", "expected"),
        [
            ("Claude usage HTTP 401: unauthorized", "login_required"),
            ("Claude usage HTTP 403: forbidden", "login_required"),
            ("token exchange invalid_grant", "login_required"),
            ("token expired", "login_required"),
            ("Claude API HTTP 429: rate_limit", "rate_limited"),
            ("Claude usage request failed: timed out", "network"),
            ("curl not found; install curl", "network"),
        ],
    )
    def test_classification(self, detail, expected):
        assert mod.classify_claude_failure(detail) == expected

    def test_token_exchange_expired_certificate_is_not_stale_code(self):
        message, cool_down = mod.claude_token_exchange_error("certificate has expired")

        assert "stale or expired OAuth code" not in message
        assert cool_down is False

    def test_token_exchange_invalid_grant_is_stale_code(self):
        message, cool_down = mod.claude_token_exchange_error("invalid_grant")

        assert "stale or expired OAuth code" in message
        assert cool_down is False


class TestParseClaudeExpiresAt:
    def test_numeric_passthrough(self):
        assert mod.parse_claude_expires_at(1700000000) == 1700000000

    def test_iso_string(self):
        assert mod.parse_claude_expires_at("2026-06-11T00:00:00+00:00") == 1781136000

    def test_z_suffix(self):
        assert mod.parse_claude_expires_at("2026-06-11T00:00:00Z") == 1781136000

    def test_invalid(self):
        assert mod.parse_claude_expires_at("") is None
        assert mod.parse_claude_expires_at("not-a-date") is None
        assert mod.parse_claude_expires_at(None) is None


class TestCredentialsFromTokenResponse:
    def test_expires_in_int(self):
        creds = mod.claude_credentials_from_token_response(
            {"access_token": "a", "refresh_token": "r", "expires_in": 3600}
        )
        assert creds["access_token"] == "a"
        assert creds["expires_at"] is not None

    def test_fallback_refresh_token(self):
        creds = mod.claude_credentials_from_token_response(
            {"access_token": "a"}, fallback={"refresh_token": "old", "expires_at": 5}
        )
        assert creds["refresh_token"] == "old"
        assert creds["expires_at"] == 5

    def test_scope_string_split(self):
        creds = mod.claude_credentials_from_token_response(
            {"access_token": "a", "scope": "user:profile user:inference"}
        )
        assert creds["scopes"] == ["user:profile", "user:inference"]


class TestWindowsProcessMatching:
    def test_codex_exe_matches(self):
        assert mod.is_windows_codex_process("Codex.exe", "C:\\x\\Codex.exe")

    def test_app_server_requires_codex_command(self):
        assert mod.is_windows_codex_process("app-server.exe", "codex app-server --port 1")
        assert not mod.is_windows_codex_process("app-server.exe", "some other app-server")

    def test_ignored_markers_win(self):
        assert not mod.is_windows_codex_process(
            "Codex.exe", "C:\\x\\Codex.exe browser_crashpad_handler"
        )

    def test_marker_only_match(self):
        assert mod.is_windows_codex_process("node.exe", "node C:\\app codex app-server")


class TestIdentitiesMatch:
    def test_match(self):
        left = {"email": "a@b.c", "plan": "plus", "organization": "Org"}
        assert mod.identities_match(left, dict(left))

    def test_email_required(self):
        assert not mod.identities_match(
            {"email": None, "plan": "plus", "organization": None},
            {"email": None, "plan": "plus", "organization": None},
        )

    def test_org_conflict(self):
        left = {"email": "a@b.c", "plan": "plus", "organization": "One"}
        right = {"email": "a@b.c", "plan": "plus", "organization": "Two"}
        assert not mod.identities_match(left, right)

    def test_one_sided_org_is_ok(self):
        left = {"email": "a@b.c", "plan": "plus", "organization": None}
        right = {"email": "a@b.c", "plan": "plus", "organization": "Two"}
        assert mod.identities_match(left, right)


class TestClaudeOAuthInput:
    def test_rejects_command_paste(self):
        with pytest.raises(SystemExit):
            mod.parse_claude_oauth_input("codex-desktop-switch claude-login")

    def test_rejects_short_code(self):
        with pytest.raises(SystemExit):
            mod.parse_claude_oauth_input("short")

    def test_url_extracts_code_and_state(self):
        code, state = mod.parse_claude_oauth_input(
            "https://platform.claude.com/oauth/code/callback?code=abcdefghijklmnopqrstu&state=xyzxyzxyzxyzxyzxyzxyz"
        )
        assert code == "abcdefghijklmnopqrstu"
        assert state == "xyzxyzxyzxyzxyzxyzxyz"

    def test_hash_separated_state(self):
        code, state = mod.parse_claude_oauth_input("abcdefghijklmnopqrstu#thestate")
        assert code == "abcdefghijklmnopqrstu"
        assert state == "thestate"


class TestUsageRows:
    def test_base_row_shape(self):
        row = mod.usage_row_base("main")
        assert row["profile"] == "main"
        assert row["error"] is None
        assert "five_hour_left" in row and "weekly_left" in row

    def test_collect_preserves_order_and_isolates_failures(self, monkeypatch, tmp_path):
        dirs = [tmp_path / "a", tmp_path / "b", tmp_path / "c"]

        def fake_row(profile_dir):
            if profile_dir.name == "b":
                raise RuntimeError("boom")
            row = mod.usage_row_base(profile_dir.name)
            row["five_hour_left"] = 50
            return row

        monkeypatch.setattr(mod, "codex_usage_row", fake_row)
        rows = mod.collect_usage_rows(dirs)
        assert [row["profile"] for row in rows] == ["a", "b", "c"]
        assert rows[0]["error"] is None
        assert "boom" in rows[1]["error"]
        assert rows[2]["five_hour_left"] == 50

    def test_collect_empty(self):
        assert mod.collect_usage_rows([]) == []


class TestBackupPruning:
    def test_keeps_newest_n(self, monkeypatch, tmp_path):
        for index in range(25):
            (tmp_path / f"20260101-0000{index:02d}").mkdir()
        monkeypatch.setattr(mod, "BACKUP_ROOT", tmp_path)

        mod.prune_backups(keep=20)

        remaining = sorted(p.name for p in tmp_path.iterdir())
        assert len(remaining) == 20
        assert remaining[0] == "20260101-000005"

    def test_missing_root_is_noop(self, monkeypatch, tmp_path):
        monkeypatch.setattr(mod, "BACKUP_ROOT", tmp_path / "missing")
        mod.prune_backups()


class TestRoundedPercent:
    def test_none(self):
        assert mod.rounded_percent(None) is None

    def test_rounds(self):
        assert mod.rounded_percent(42.4) == 42
        assert mod.rounded_percent(42.5) == 42 or mod.rounded_percent(42.5) == 43
