import importlib.util, json, os, shutil, tempfile, unittest

spec = importlib.util.spec_from_file_location('check_iis_publish', os.path.join(os.path.dirname(__file__), '..', 'check-iis-publish.py'))
ci = importlib.util.module_from_spec(spec); spec.loader.exec_module(ci)

WEB = """<?xml version="1.0"?><configuration><location path="." inheritInChildApplications="false"><system.webServer>
<handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers>
<aspNetCore processPath="dotnet" arguments=".\\Ecp.Host.dll" hostingModel="inprocess" stdoutLogEnabled="false">{env}</aspNetCore>
<security><requestFiltering><requestLimits maxAllowedContentLength="{default}" /></requestFiltering></security></system.webServer></location>
{extra}</configuration>"""
SETTINGS = {"Ecp": {"Mode": "Production", "DemoSeed": False, "DemoTenantResolution": False},
            "Database": {"Provider": "SqlServer", "ConnectionString": "", "AutoMigrate": False}}


def make(d, env="", default=10485760, extra="", settings=SETTINGS, files=()):
    open(os.path.join(d, "Ecp.Host.dll"), "w").close()
    open(os.path.join(d, "web.config"), "w").write(WEB.format(env=env, default=default, extra=extra))
    json.dump(settings, open(os.path.join(d, "appsettings.json"), "w"))
    os.makedirs(os.path.join(d, "packs")); [open(os.path.join(d, "packs", n), "w").close() for n in ("a.ecpkg", "b.ecpkg")]
    for f in files: os.makedirs(os.path.dirname(os.path.join(d, f)) or d, exist_ok=True); open(os.path.join(d, f), "w").close()


def run(**kw):
    d = tempfile.mkdtemp()
    try:
        make(d, **kw); return ci.inspect(d)
    finally: shutil.rmtree(d)


def loc(path, value): return f'<location path="{path}"><system.webServer><security><requestFiltering><requestLimits maxAllowedContentLength="{value}" /></requestFiltering></security></system.webServer></location>'


class IisPublishCheckTests(unittest.TestCase):
    def test_a_good_publish_passes(self):
        self.assertEqual(run(extra=loc("Migration/Batches", 2147483648) + loc("Contracts/Documents", 105906176)), [])
    def test_secret_like_environment_variable_is_refused(self):
        self.assertTrue(any("secret-like" in p for p in run(env='<environmentVariables><environmentVariable name="Database__Password" value="x" /></environmentVariables>')))
    def test_development_environment_is_refused(self):
        self.assertTrue(any("Development" in p for p in run(env='<environmentVariables><environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Development" /></environmentVariables>')))
    def test_large_default_limit_is_refused(self):
        self.assertTrue(any("default maxAllowedContentLength" in p for p in run(default=2147483648)))
    def test_only_the_migration_url_may_be_huge(self):
        self.assertTrue(any("only the migration" in p for p in run(extra=loc("Contracts/Documents", 2147483648))))
    def test_demo_defaults_and_development_file_are_refused(self):
        bad = {"Ecp": {"Mode": "Demo", "DemoSeed": True, "DemoTenantResolution": True}, "Database": {"Provider": "Sqlite", "ConnectionString": "Data Source=x", "AutoMigrate": True}}
        problems = run(settings=bad, files=["appsettings.Development.json", "data/x.db"])
        for text in ("Production", "demonstration", "SqlServer", "automatic migration", "inline connection string", "Development.json", "data folder"):
            self.assertTrue(any(text in p for p in problems), text)

if __name__ == "__main__": unittest.main()
