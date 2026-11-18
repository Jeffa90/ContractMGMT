import importlib.util, json, os, shutil, subprocess, tempfile, unittest

spec = importlib.util.spec_from_file_location('check_secrets', os.path.join(os.path.dirname(__file__), '..', 'check-secrets.py'))
cs = importlib.util.module_from_spec(spec); spec.loader.exec_module(cs)


def scan(files, allowlist=None):
    d = tempfile.mkdtemp()
    try:
        subprocess.run(["git", "init", "-q", d], check=True)
        for name, text in files.items():
            path = os.path.join(d, name); os.makedirs(os.path.dirname(path), exist_ok=True)
            open(path, "w", encoding="utf-8").write(text)
        if allowlist is not None:
            os.makedirs(os.path.join(d, "tools"), exist_ok=True)
            json.dump(allowlist, open(os.path.join(d, "tools", "secrets-allowlist.json"), "w"))
        subprocess.run(["git", "-C", d, "add", "-A"], check=True)
        return [(rel, rule) for rel, _, rule in cs.scan(d)]
    finally:
        shutil.rmtree(d)


class SecretScanTests(unittest.TestCase):
    # Values are built at run time so that this file itself never contains anything that looks like a secret.
    def test_token_formats_are_found(self):
        github = "gh" + "p_" + "a1B2" * 9
        aws = "AK" + "IA" + "ABCDEFGH" * 2
        found = scan({"app/config.txt": f"token {github}\nkey {aws}\n"})
        self.assertIn(("app/config.txt", "github-token"), found); self.assertIn(("app/config.txt", "aws-access-key-id"), found)

    def test_a_private_key_block_is_found(self):
        self.assertEqual(scan({"k.pem": "-----BEGIN " + "PRIVATE KEY-----\nabc\n"}), [("k.pem", "private-key")])

    def test_a_secret_assignment_with_a_real_looking_value_is_found_in_code_and_in_configuration(self):
        value = "q7Zr" + "9xLm" + "T2vB"
        self.assertEqual(scan({"src/A.cs": f'var password = "{value}";\n'}), [("src/A.cs", "secret-assignment")])
        self.assertEqual(scan({"appsettings.json": f'{{ "ClientSecret": "{value}" }}\n'}), [("appsettings.json", "secret-assignment")])
        self.assertEqual(scan({"deploy.env": f"DB_PASSWORD={value}\n"}), [("deploy.env", "secret-assignment")])

    def test_references_expressions_and_synthetic_values_are_not_findings(self):
        self.assertEqual(scan({"src/A.cs": 'options.ClientSecret = secrets.Resolve(reference);\nvar pwd = "not-a-real-secret-value";\n'}), [])
        self.assertEqual(scan({"x.json": '{ "ClientSecretRef": "env:ECP_OIDC_SECRET", "Password": "${DB_PASSWORD}" }\n'}), [])
        self.assertEqual(scan({"README.md": "Never commit a password. Set Database__ConnectionStringRef to a file reference.\n"}), [])

    def test_an_allowance_needs_a_reason_and_applies_only_to_its_rule(self):
        value = "q7Zr" + "9xLm" + "T2vB"
        files = {"tests/T.cs": f'var password = "{value}";\n'}
        self.assertEqual(scan(files, [{"path_prefix": "tests/", "rule": "secret-assignment", "reason": "canary values"}]), [])
        with self.assertRaises(SystemExit): scan(files, [{"path_prefix": "tests/", "rule": "secret-assignment"}])
        github = "gh" + "p_" + "a1B2" * 9
        self.assertEqual(scan({"tests/T.cs": f"// {github}\n"}, [{"path_prefix": "tests/", "rule": "secret-assignment", "reason": "canary values"}]), [("tests/T.cs", "github-token")])

    def test_the_repository_itself_is_clean(self):
        root = os.path.join(os.path.dirname(__file__), '..', '..')
        self.assertEqual(cs.scan(root), [])

if __name__ == "__main__": unittest.main()
