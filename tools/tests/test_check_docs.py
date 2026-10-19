"""Tests for tools/check-docs.py (G-36): a guide that names a page, a permission or a retired statement the application does not support must fail the check."""
import importlib.util, os, tempfile, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
spec = importlib.util.spec_from_file_location('check_docs', os.path.join(ROOT, 'tools', 'check-docs.py'))
check_docs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check_docs)


def run(text, config=None, guide='docs/BUSINESS_ADMIN_GUIDE.md'):
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, guide)
        os.makedirs(os.path.dirname(path))
        with open(path, 'w', encoding='utf-8') as f: f.write(text)
        return check_docs.check(root=d, guides=[guide], config=config or {})


class CheckDocsTests(unittest.TestCase):
    def test_the_repository_guides_are_consistent(self):
        self.assertEqual([], check_docs.check())

    def test_a_real_page_passes_and_an_invented_page_fails(self):
        self.assertEqual([], run('Open /Admin/Operations/Email to see it.'))
        problems = run('Open /Admin/Operations/Telepathy to see it.')
        self.assertEqual(1, len(problems))
        self.assertIn('not a page of the application', problems[0])

    def test_an_invented_permission_fails(self):
        self.assertEqual([], run('You need `operations.read`.'))
        self.assertEqual(1, len(run('You need `operations.levitate`.')))

    def test_a_command_in_a_business_guide_must_be_marked_technical(self):
        self.assertEqual(1, len(run('Run `dotnet run` to sign the pack.')))
        self.assertEqual([], run('Technical task for the System Administrator: run `docker compose up`.'))
        self.assertEqual([], run('A business administrator must not run `curl` against the host.'))

    def test_a_forbidden_statement_fails_and_a_missing_required_statement_fails(self):
        cfg = {'forbidden': [{'pattern': 'cannot succeed in Production', 'files': ['docs/BUSINESS_ADMIN_GUIDE.md'], 'why': 'fixed'}],
               'required': [{'file': 'docs/BUSINESS_ADMIN_GUIDE.md', 'pattern': 'Ecp:Governance', 'why': 'documented'}]}
        problems = run('Publish cannot succeed in Production.', cfg)
        self.assertEqual(2, len(problems))
        self.assertEqual([], run('Set Ecp:Governance:SecondPerson.', cfg))

    def test_a_missing_guide_is_reported(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(1, len(check_docs.check(root=d, guides=['docs/NOPE.md'], config={})))


if __name__ == '__main__':
    unittest.main()
