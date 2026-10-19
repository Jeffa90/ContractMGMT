# ecp-pack

Build, validate, sign, verify and inspect Enterprise Contract Platform solution packs (`.ecpkg`).

This is a technical tool for the **Pack Publisher**. It needs the .NET runtime only: no source checkout, no Visual Studio, no SDK.

```
dotnet tool install --global Ecp.PackTool
ecp-pack version
ecp-pack build --source ./my-pack --out my-pack-1.0.0.ecpkg
ecp-pack validate --package my-pack-1.0.0.ecpkg
ecp-pack sign --package my-pack-1.0.0.ecpkg --out my-pack-1.0.0.signed.ecpkg --key-id publisher-2026 --key-ref env:ECP_PACK_KEY
ecp-pack verify --package my-pack-1.0.0.signed.ecpkg --public-key publisher.pub.pem --key-id publisher-2026 --production
ecp-pack report --package my-pack-1.0.0.signed.ecpkg --out report.json
```

The private key is referenced (`env:NAME` or `file:PATH`), never typed, printed, stored or uploaded. Signing happens where the key is kept, never in a browser. Exit codes: 0 success, 1 not acceptable, 2 usage, 3 environment (file or key).

See docs/PACK_SIGNING.md in the platform documentation for roles, key custody and the full procedure.
