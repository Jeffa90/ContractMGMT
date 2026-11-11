#!/usr/bin/env python3
"""Inspect a `dotnet publish` output folder of src/Ecp.Host for IIS (ASP.NET Core Module V2, in-process).

This checks the published files only. It does NOT prove the site runs on IIS: that needs a Windows host with the .NET Hosting Bundle.
Usage: check-iis-publish.py <publish-folder>
"""
import json, os, re, sys
import xml.etree.ElementTree as ET

MIB = 1024 * 1024
SECRET_NAME = re.compile(r"(password|secret|connectionstring|apikey|token|pwd)", re.I)


def inspect(folder):
    problems = []
    def need(cond, msg):
        if not cond: problems.append(msg)

    need(os.path.isfile(os.path.join(folder, "Ecp.Host.dll")), "Ecp.Host.dll is missing from the publish folder")
    wc = os.path.join(folder, "web.config")
    if not os.path.isfile(wc):
        problems.append("web.config was not published"); return problems
    root = ET.parse(wc).getroot()

    handlers = [h.attrib for h in root.iter("add") if h.attrib.get("name") == "aspNetCore"]
    need(handlers and handlers[0].get("modules") == "AspNetCoreModuleV2", "the aspNetCore handler must use AspNetCoreModuleV2")
    asp = list(root.iter("aspNetCore"))
    need(len(asp) == 1, "exactly one aspNetCore element is required")
    if asp:
        a = asp[0].attrib
        need(a.get("hostingModel", "").lower() == "inprocess", "hostingModel must be inprocess")
        need(a.get("processPath") in ("dotnet", ".\\Ecp.Host.exe"), "processPath must be dotnet (framework-dependent) or Ecp.Host.exe")
        need("Ecp.Host.dll" in a.get("arguments", "") or a.get("processPath", "").endswith("Ecp.Host.exe"), "arguments must start Ecp.Host.dll")
        need(a.get("stdoutLogEnabled", "false").lower() == "false", "stdout logging must be off (the application writes its own redacted log)")
        for env in asp[0].iter("environmentVariable"):
            n = env.attrib.get("name", "")
            need(not SECRET_NAME.search(n), f"web.config must not carry a secret-like environment variable: {n}")
            need(n != "ASPNETCORE_ENVIRONMENT" or env.attrib.get("value", "").lower() != "development", "web.config must not set the Development environment")

    # request limits: small default, the migration archive URL is the only one near 2 GiB, nothing above 2 GiB
    limits = {}
    for loc in root.iter("location"):
        for lim in loc.iter("requestLimits"):
            limits[loc.attrib.get("path", ".")] = int(lim.attrib.get("maxAllowedContentLength", "0"))
    need(limits.get(".", 0) and limits["."] <= 10 * MIB, "the default maxAllowedContentLength must be 10 MiB or less")
    for path, value in limits.items():
        need(value <= 2048 * MIB, f"{path}: limit above 2 GiB")
        need(value < 1024 * MIB or path == "Migration/Batches", f"{path}: only the migration archive URL may be opened beyond 1 GiB")

    settings = os.path.join(folder, "appsettings.json")
    need(os.path.isfile(settings), "appsettings.json is missing")
    if os.path.isfile(settings):
        cfg = json.load(open(settings, encoding="utf-8-sig"))
        ecp, db = cfg.get("Ecp", {}), cfg.get("Database", {})
        need(ecp.get("Mode") == "Production", "the published appsettings.json must default to Production")
        need(ecp.get("DemoSeed") is False and ecp.get("DemoTenantResolution") is False, "demonstration seeding/identity must default to off")
        need(db.get("Provider") == "SqlServer", "the published database provider must default to SqlServer")
        need(db.get("AutoMigrate") is False, "automatic migration must default to off")
        need(not db.get("ConnectionString"), "no inline connection string may be published")
    need(not os.path.exists(os.path.join(folder, "appsettings.Development.json")), "appsettings.Development.json must not be published")
    need(not os.path.exists(os.path.join(folder, "data")), "a local data folder must not be published")
    packs = [f for f in os.listdir(os.path.join(folder, "packs"))] if os.path.isdir(os.path.join(folder, "packs")) else []
    need(sum(f.endswith(".ecpkg") for f in packs) >= 2, "the shipped .ecpkg solution packs are missing")
    return problems


def main(argv):
    if len(argv) != 2:
        print(__doc__); return 2
    problems = inspect(argv[1])
    for p in problems: print("FAIL:", p)
    print("IIS publish check:", "FAILED" if problems else "passed (files only; not an IIS run)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
