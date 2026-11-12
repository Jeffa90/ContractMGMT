import os, re, subprocess, unittest

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
SCRIPT = os.path.join(ROOT, 'scripts', 'setup', 'setup-github-environments.sh')
EXAMPLE = os.path.join(ROOT, 'scripts', 'setup', 'environments.example.env')
text = open(SCRIPT, encoding='utf-8').read()


class SetupScriptSafetyTests(unittest.TestCase):
    """The script is never executed by these tests (it talks to GitHub). They parse it and check that each safeguard is present and that no placeholder is ever written."""

    def test_it_is_valid_bash(self):
        self.assertEqual(subprocess.run(['bash', '-n', SCRIPT], capture_output=True).returncode, 0)

    def test_it_stops_on_errors_and_unset_variables(self):
        self.assertIn('set -euo pipefail', text)

    def test_a_placeholder_is_detected_and_never_written(self):
        self.assertIn('is_placeholder()', text)
        # The only place the phrase appears is the detector; no variable is ever set to it.
        for line in text.splitlines():
            if re.search(r'update me', line, re.I):
                self.assertTrue('updateme' in line.lower() or line.lstrip().startswith('#'), line)
        self.assertNotRegex(text, r'setvar\s+\S+\s+\S+\s+"UPDATE ME"')
        self.assertIn('Refusing to write a placeholder', text)

    def test_missing_values_stop_the_run_before_anything_is_changed(self):
        self.assertIn('MISSING required values', text)
        stop = text.index('if [ "${#MISSING[@]}" -gt 0 ] || [ "${#ERRORS[@]}" -gt 0 ]; then')
        first_write = min(i for i in (text.index('gh variable set'), text.index('gh api -X PUT')) if i > 0)
        self.assertLess(stop, first_write)
        self.assertIn('exit 2', text[stop:stop + 400])

    def test_an_existing_value_is_not_overwritten_without_being_named(self):
        self.assertIn('--overwrite', text)
        self.assertIn('KEPT (already set to a different value', text)
        self.assertIn('may_overwrite', text)

    def test_production_needs_an_explicit_reviewer_who_is_not_the_person_running_it(self):
        self.assertIn('PROD_REVIEWER', text)
        self.assertIn('Production approval must be given by someone else', text)
        self.assertIn('prevent_self_review', text)
        self.assertNotRegex(text, r'PROD_REVIEWER:-\S')   # no default reviewer

    def test_deployment_branches_are_named_validated_and_enforced(self):
        self.assertIn('QA_BRANCH', text); self.assertIn('PROD_BRANCH', text)
        self.assertIn('that branch does not exist', text)
        self.assertIn('custom_branch_policies', text)
        self.assertIn('deployment-branch-policies', text)

    def test_a_dry_run_changes_nothing(self):
        self.assertIn('--dry-run', text)
        # every write goes through a function that checks DRY_RUN, or is itself guarded
        for needle in ('gh variable set', 'gh api -X PUT', 'gh api -X DELETE', 'gh api -X POST'):
            for m in re.finditer(re.escape(needle), text):
                window = text[max(0, m.start() - 900):m.start()]
                self.assertIn('DRY_RUN', window, f'{needle} is not guarded by DRY_RUN')

    def test_secrets_are_never_read_or_written_only_their_names_are_checked(self):
        self.assertNotIn('gh secret set "$', text.replace('echo "gh secret set', ''))
        self.assertIn('gh secret list', text)
        self.assertIn('IIS_APP_POOL_PASSWORD', text)

    def test_every_variable_and_secret_the_pipeline_reads_is_listed(self):
        wf = open(os.path.join(ROOT, '.github', 'workflows', 'iis-deploy.yml'), encoding='utf-8').read()
        for name in set(re.findall(r'vars\.([A-Z_]+)', wf)) | set(re.findall(r'secrets\.([A-Z_]+)', wf)):
            self.assertTrue(name in text, f'{name} is read by the deploy workflow but not listed by the setup script')

    def test_the_example_values_file_lists_every_required_key_and_holds_no_value(self):
        keys = {line.split('=')[0] for line in open(EXAMPLE, encoding='utf-8') if '=' in line and not line.startswith('#')}
        required = re.findall(r'"([A-Z_]+)\|', text[text.index('REQUIRED_PER_ENV=('):text.index('OPTIONAL_PER_ENV=(')])
        for k in required:
            self.assertTrue(k in keys or f'QA_{k}' in keys, k)
        for line in open(EXAMPLE, encoding='utf-8'):
            if '=' in line and not line.startswith('#'):
                self.assertEqual(line.strip().split('=', 1)[1], '', line)   # a template, never a real value

    def test_the_connection_string_written_holds_no_credential(self):
        self.assertIn('Integrated Security=True', text)
        self.assertNotRegex(text, r'(?i)password\s*=')
        self.assertIn('TRUST_SERVER_CERTIFICATE', text)

if __name__ == '__main__':
    unittest.main()
