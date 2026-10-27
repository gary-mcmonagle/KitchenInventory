"""Tests for deploy/seed_ai_models.py (#120). Run: python3 -m unittest deploy/tests/test_seed_ai_models.py"""

import copy
import json
import sys
import unittest
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import seed_ai_models as seed  # noqa: E402

NOW = datetime(2026, 10, 27, 9, 0, 0, 123000, tzinfo=timezone.utc)
OID = "caf87c93-9030-44e8-a25d-fed63261ea67"


def model(id, deployment, mode="usual", jobs=(), tests=None, label=None):
    return {"id": id, "label": label or id, "deployment": deployment, "temperatureMode": mode, "jobs": list(jobs),
            "addedBy": OID, "addedByName": "Gary", "addedAt": "2026-10-26T09:00:00.000Z", "tests": tests}


def wanted(id, deployment, mode="usual", jobs=("recipes",), label=None):
    return {"id": id, "label": label or id.upper(), "deployment": deployment, "temperatureMode": mode, "jobs": list(jobs)}


def run(existing, *models):
    return seed.merge(existing, {"models": list(models)}, OID, "Gary", NOW)


class SeedFile(unittest.TestCase):
    def test_the_committed_seed_file_is_valid(self):
        path = Path(__file__).resolve().parents[1] / "ai-models.seed.json"
        self.assertEqual([], seed.check_seed(json.loads(path.read_text())))

    def test_the_committed_seed_makes_luna_default_temperature_and_deepseek_usual(self):
        path = Path(__file__).resolve().parents[1] / "ai-models.seed.json"
        models = {m["id"]: m for m in json.loads(path.read_text())["models"]}
        self.assertEqual("default", models["gpt-6-luna"]["temperatureMode"])
        self.assertEqual("DeepSeek-V4.1-Flash", models["deepseek-v4-1-flash"]["deployment"])
        self.assertEqual("usual", models["deepseek-v4-1-flash"]["temperatureMode"])

    def test_problems_are_named(self):
        bad = {"models": [
            {"id": "a b", "label": "", "deployment": "has space", "temperatureMode": "hot", "jobs": ["everything"]},
            wanted("dup", "same"), wanted("dup", "SAME"),
        ]}
        text = " ".join(seed.check_seed(bad))
        for expected in ("id must be", "label must be", "deployment must be", "temperatureMode must be", "jobs must be", "id repeated", "deployment repeated"):
            self.assertIn(expected, text)
        self.assertTrue(seed.check_seed({}))
        self.assertTrue(seed.check_seed({"models": []}))


class Merge(unittest.TestCase):
    def test_with_no_document_it_creates_one_with_the_models_added_by_the_person(self):
        doc, changes = run(None, wanted("luna", "gpt-6-luna", "default"), wanted("deepseek", "DeepSeek-V4.1-Flash", jobs=("recipes", "genericNames")))

        self.assertEqual(("aiModels", "aiModels", {}), (doc["id"], doc["type"], doc["active"]))
        luna, deepseek = doc["models"]
        self.assertEqual(("luna", "gpt-6-luna", "default", ["recipes"], OID, "Gary", "2026-10-27T09:00:00.123Z", None),
                         (luna["id"], luna["deployment"], luna["temperatureMode"], luna["jobs"], luna["addedBy"], luna["addedByName"], luna["addedAt"], luna["tests"]))
        self.assertEqual(["recipes", "genericNames"], deepseek["jobs"])
        self.assertEqual(2, sum(c.startswith("add ") for c in changes))

    def test_it_keeps_everything_it_wasnt_asked_about(self):
        active = {"recipes": {"modelId": "other", "changedBy": OID, "changedByName": "Gary", "changedAt": "2026-10-26T09:00:00.000Z", "previousModelId": None}}
        existing = {"id": "aiModels", "type": "aiModels", "models": [model("other", "other-dep", jobs=["recipes"], label="Mine")], "active": active}
        before = copy.deepcopy(existing)

        doc, _ = run(existing, wanted("luna", "gpt-6-luna", "default"))

        self.assertEqual(before, existing)  # the input isn't changed
        self.assertEqual(active, doc["active"])
        self.assertEqual(["other", "luna"], [m["id"] for m in doc["models"]])
        self.assertEqual("Mine", doc["models"][0]["label"])

    def test_a_registered_model_gets_its_temperature_and_jobs_set_but_keeps_its_label_and_who_added_it(self):
        existing = {"id": "aiModels", "type": "aiModels", "models": [model("luna", "gpt-6-luna", "usual", [], label="Renamed")], "active": {}}

        doc, changes = run(existing, wanted("luna", "gpt-6-luna", "default", ("recipes",), label="GPT-6 Luna"))

        luna = doc["models"][0]
        self.assertEqual(("Renamed", "default", ["recipes"], OID), (luna["label"], luna["temperatureMode"], luna["jobs"], luna["addedBy"]))
        self.assertEqual(["set luna temperature usual -> default", "set luna jobs none -> recipes"], changes)

    def test_running_it_again_changes_nothing(self):
        doc, _ = run(None, wanted("luna", "gpt-6-luna", "default"))

        again, changes = run(doc, wanted("luna", "gpt-6-luna", "default"))

        self.assertEqual(doc, again)
        self.assertFalse(any(c.startswith(("add", "set")) for c in changes))

    def test_a_model_with_test_results_is_left_to_the_test_suite(self):
        existing = {"id": "aiModels", "type": "aiModels", "models": [model("luna", "gpt-6-luna", "default", ["recipes", "genericNames"], tests={"passed": True})], "active": {}}

        doc, changes = run(existing, wanted("luna", "gpt-6-luna", "usual", ("photos",)))

        self.assertEqual(existing, doc)
        self.assertEqual(["skip luna: it has test results, which now own its temperature and jobs"], changes)

    def test_it_refuses_a_changed_deployment_a_duplicate_deployment_and_too_many_models(self):
        existing = {"id": "aiModels", "type": "aiModels", "models": [model("luna", "gpt-6-luna")], "active": {}}
        with self.assertRaisesRegex(ValueError, "can't change"):
            run(existing, wanted("luna", "gpt-6-sol"))
        with self.assertRaisesRegex(ValueError, "already registered as 'luna'"):
            run(existing, wanted("second", "GPT-6-LUNA"))
        full = {"id": "aiModels", "type": "aiModels", "models": [model(f"m{i}", f"dep-{i}") for i in range(seed.MAX_MODELS)], "active": {}}
        with self.assertRaisesRegex(ValueError, "At most 20"):
            run(full, wanted("one-more", "dep-extra"))

    def test_the_document_has_the_shape_the_app_reads(self):
        doc, _ = run(None, wanted("luna", "gpt-6-luna", "default"))

        self.assertEqual({"id", "type", "models", "active"}, set(doc))
        self.assertEqual({"id", "label", "deployment", "temperatureMode", "jobs", "addedBy", "addedByName", "addedAt", "tests"}, set(doc["models"][0]))
        self.assertIn(doc["models"][0]["temperatureMode"], seed.MODES)


class FakeCosmos(seed.Cosmos):
    """The REST client with the network and az replaced: answers what the test says."""

    def __init__(self, *answers):
        self.base, self.database, self.auth = "https://cosmos.example.test", "cupboard", "x"
        self.answers = list(answers)
        self.sent = []

    def _send(self, method, path, body=None, etag=None):
        self.sent.append((method, path, etag))
        return self.answers.pop(0)


class Writing(unittest.TestCase):
    def test_a_new_document_is_created_and_an_existing_one_replaced_under_its_etag(self):
        created = FakeCosmos((201, {}, "e1"))
        created.write({"id": "aiModels"}, None)
        self.assertEqual([("POST", "", None)], created.sent)

        replaced = FakeCosmos((200, {}, "e2"))
        replaced.write({"id": "aiModels"}, "e1")
        self.assertEqual([("PUT", "/aiModels", "e1")], replaced.sent)

    def test_a_missing_role_is_a_permission_error_not_a_crash(self):
        with self.assertRaises(PermissionError) as raised:
            FakeCosmos((403, {"error": "denied"}, None)).write({"id": "aiModels"}, "e1")
        self.assertIn("Data Contributor", str(raised.exception))

    def test_someone_elses_change_in_between_asks_for_a_rerun(self):
        for status in (409, 412):
            with self.assertRaisesRegex(RuntimeError, "Run it again"):
                FakeCosmos((status, {"error": "x"}, None)).write({"id": "aiModels"}, "e1")

    def test_anything_else_is_an_error_with_the_status(self):
        with self.assertRaisesRegex(RuntimeError, "HTTP 500"):
            FakeCosmos((500, {"error": "boom"}, None)).write({"id": "aiModels"}, None)

    def test_reading_a_missing_document_gives_none_and_a_found_one_its_etag(self):
        self.assertEqual((None, None), FakeCosmos((404, {}, None)).read())
        self.assertEqual(({"id": "aiModels"}, "e9"), FakeCosmos((200, {"id": "aiModels"}, "e9")).read())
        with self.assertRaisesRegex(RuntimeError, "HTTP 401"):
            FakeCosmos((401, {"error": "x"}, None)).read()


if __name__ == "__main__":
    unittest.main()
