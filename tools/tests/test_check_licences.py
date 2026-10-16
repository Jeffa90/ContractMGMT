import json, os, sys, tempfile, unittest, io, contextlib
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))
import importlib.util
spec = importlib.util.spec_from_file_location('check_licences', os.path.join(os.path.dirname(__file__), '..', 'check-licences.py'))
cl = importlib.util.module_from_spec(spec); spec.loader.exec_module(cl)

POLICY = {"allowed": ["MIT"], "restricted": ["MPL-2.0"], "prohibited": ["GPL-3.0-only"], "urlMappings": {"http://x/mit": "MIT"},
          "exceptions": [{"package": "Odd", "resolvedLicence": "MIT", "scope": "test", "status": "Recorded", "evidence": "e"},
                         {"package": "Ms", "resolvedLicence": "LicenseRef-MS", "scope": "shipped", "status": "PendingOwnerReview", "evidence": "e"}]}

def run(packages, policy=POLICY):
    with tempfile.TemporaryDirectory() as d:
        pp, lp = os.path.join(d, 'p.json'), os.path.join(d, 'l.json')
        with open(pp, 'w') as f: json.dump(policy, f)
        with open(lp, 'w') as f: json.dump(packages, f)
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            code = cl.main(lp, pp)
        return code, buf.getvalue()

def pkg(name, lic): return {"PackageId": name, "PackageVersion": "1.0.0", "License": lic}

class LicencePolicyTests(unittest.TestCase):
    def test_allowed_passes(self):
        self.assertEqual(run([pkg("A", "MIT"), pkg("Odd", None), pkg("Ms", "x")])[0], 0)
    def test_url_mapping(self):
        self.assertEqual(run([pkg("A", "http://x/mit"), pkg("Odd", None), pkg("Ms", None)])[0], 0)
    def test_prohibited_fails(self):
        code, out = run([pkg("A", "GPL-3.0-only"), pkg("Odd", None), pkg("Ms", None)])
        self.assertEqual(code, 1); self.assertIn("prohibited", out)
    def test_restricted_without_exception_fails(self):
        self.assertEqual(run([pkg("A", "MPL-2.0"), pkg("Odd", None), pkg("Ms", None)])[0], 1)
    def test_unknown_fails(self):
        code, out = run([pkg("A", "Weird-1.0"), pkg("Odd", None), pkg("Ms", None)])
        self.assertEqual(code, 1); self.assertIn("not in the policy", out)
    def test_missing_licence_fails(self):
        self.assertEqual(run([pkg("A", None), pkg("Odd", None), pkg("Ms", None)])[0], 1)
    def test_stale_exception_fails(self):
        code, out = run([pkg("A", "MIT")])
        self.assertEqual(code, 1); self.assertIn("stale exception", out)
    def test_pending_review_warns_but_does_not_fail(self):
        code, out = run([pkg("Odd", None), pkg("Ms", None)])
        self.assertEqual(code, 0); self.assertIn("PENDING OWNER REVIEW", out)

if __name__ == '__main__':
    unittest.main()
