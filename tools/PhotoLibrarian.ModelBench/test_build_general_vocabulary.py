import copy
import unittest

from build_general_vocabulary import apply_overrides, compile_taxonomy


class GeneralVocabularyTests(unittest.TestCase):
    def setUp(self):
        self.taxonomy = {
            "schemaVersion": 2,
            "source": {"labelAssetSha256": "a" * 64},
            "ramLabels": ["Animal", "Bird", "Eagle", "Bald_eagle", "sea eagle", "belief"],
            "previousTinyClipLabels": ["bird"],
            "stablePaths": {"animal": "animal", "bird": "animal/bird"},
            "entries": {
                "animal": {"parent": None, "automatic": True},
                "bird": {"parent": "animal", "automatic": True},
                "eagle": {"parent": "bird", "automatic": True},
                "bald eagle": {"parent": "eagle", "automatic": True},
                "sea eagle": {"canonical": "eagle", "automatic": True},
                "belief": {"parent": None, "automatic": False, "reason": "Not an observable photo subject"},
            },
        }

    def test_compiles_full_sources_without_sample_observations(self):
        hierarchy, vocabulary, profile, mapping = compile_taxonomy(self.taxonomy)
        self.assertEqual("animal/bird/eagle/bald eagle", hierarchy["bald eagle"])
        self.assertEqual("animal/bird/eagle", hierarchy["sea eagle"])
        self.assertEqual("belief", hierarchy["belief"])
        self.assertEqual(["animal", "bald eagle", "bird", "eagle"], vocabulary["labels"])
        self.assertFalse(vocabulary["productionApproved"])
        self.assertEqual("eagle", profile["ramLabelMappings"]["sea eagle"])
        self.assertNotIn("belief", profile["ramLabelMappings"])
        self.assertEqual("", mapping["5"])
        self.assertEqual("bald eagle", mapping["3"])
        self.assertEqual(64, len(profile["vocabularySha256"]))

    def test_missing_source_label_is_an_error_even_when_not_eligible(self):
        del self.taxonomy["entries"]["belief"]
        with self.assertRaisesRegex(ValueError, "complete source"):
            compile_taxonomy(self.taxonomy)

    def test_cross_part_cycles_are_rejected(self):
        self.taxonomy["entries"]["animal"]["parent"] = "eagle"
        with self.assertRaisesRegex(ValueError, "cycle"):
            compile_taxonomy(self.taxonomy)

    def test_unknown_parents_and_unexplained_exclusions_are_rejected(self):
        for change in (
                {"parent": "missing", "automatic": True},
                {"parent": "animal", "automatic": False},
                {"parent": "animal", "canonical": "eagle", "automatic": True}):
            with self.subTest(change=change):
                value = copy.deepcopy(self.taxonomy)
                value["entries"]["bird"] = change
                with self.assertRaises(ValueError):
                    compile_taxonomy(value)

    def test_canonical_exclusion_cannot_be_bypassed_by_an_alias(self):
        self.taxonomy["entries"]["eagle"].update(automatic=False, reason="Test exclusion")
        _, vocabulary, profile, _ = compile_taxonomy(self.taxonomy)
        self.assertNotIn("sea eagle", profile["ramLabelMappings"])
        self.assertNotIn("eagle", vocabulary["labels"])
        self.assertIn("Canonical label", vocabulary["excludedLabels"]["sea eagle"])

    def test_stable_parent_paths_do_not_drift_when_vocabulary_expands(self):
        self.taxonomy["entries"]["bird"]["parent"] = None
        with self.assertRaisesRegex(ValueError, "Stable hierarchy changed"):
            compile_taxonomy(self.taxonomy)

    def test_versioned_corrections_preserve_source_labels_and_change_candidates(self):
        revised = apply_overrides(self.taxonomy, {
            "hierarchyVersion": "hierarchy-v3", "vocabularyName": "general-home-photo-v2",
            "exclude": {"sea eagle": "Excluded by explicit review"},
            "rename": {"bald eagle": "bald eagle corrected"},
        })
        hierarchy, vocabulary, profile, mapping = compile_taxonomy(revised)
        self.assertEqual("hierarchy-v3", profile["taxonomyVersion"])
        self.assertEqual("general-home-photo-v2", vocabulary["name"])
        self.assertEqual("animal/bird/eagle/bald eagle corrected", hierarchy["bald eagle"])
        self.assertEqual("bald eagle corrected", mapping["3"])
        self.assertEqual("", mapping["4"])
        self.assertNotIn("bald eagle", vocabulary["labels"])
        self.assertIn("bald eagle corrected", vocabulary["labels"])
        self.assertEqual(6, len(profile["ramLabels"]))
        self.assertTrue(self.taxonomy["entries"]["sea eagle"]["automatic"])


if __name__ == "__main__":
    unittest.main()
