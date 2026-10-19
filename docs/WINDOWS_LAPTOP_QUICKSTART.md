# Windows 11 laptop quick start: install and set up Northwind Traders

For: Jeff (Procurement Excellence Manager). One laptop, Windows 11, SQL Server Developer Edition, no Docker, this computer only (`localhost`). About 45 minutes.

**Status.** The steps were run end to end on Linux against SQL Server 2022 (the same commands and the same wizard answers; the run created a database, applied the tables, completed the 14-step wizard and checked the health page). They have **not** been run on Windows 11 itself, and the Windows sign-in connection string in step 3 is **Not Verified**. This sets up a **Test-mode trial with synthetic data only**. Do not load real contracts. It is not a production installation, and nothing here approves one.

**You do not need Visual Studio.** The platform is built on .NET 10, and Visual Studio 2022 cannot open or build .NET 10 projects. Everything below uses PowerShell, which comes with Windows.

---

## 1. Install the prerequisites

1. **.NET 10 SDK.** Download the Windows x64 **SDK** installer from <https://dotnet.microsoft.com/download/dotnet/10.0> and run it.
2. **Git for Windows.** Download from <https://git-scm.com/download/win> and accept the defaults.
3. **SQL Server 2022 Developer.** Download from <https://www.microsoft.com/sql-server/sql-server-downloads>, choose **Developer**, then **Basic** install. Accept the licence and defaults. Basic makes your own Windows account an administrator of the database server.
4. Open **PowerShell** (Start menu, type *PowerShell*) and check each one. Close and reopen PowerShell first if a command is not found.

```powershell
dotnet --version        # should start with 10.
git --version
sqlcmd -?               # should print help, not an error
```

5. Get the code (GitHub may ask you to sign in):

```powershell
cd $HOME
git clone --branch claude/stoic-volta-dvszm5 https://github.com/Jeffa90/ContractMGMT.git
cd ContractMGMT
```

## 2. Create the SQL database

```powershell
sqlcmd -S localhost -E -C -Q "CREATE DATABASE ecp_northwind"
sqlcmd -S localhost -E -C -Q "SELECT name FROM sys.databases WHERE name = 'ecp_northwind'"
```

The second command should print `ecp_northwind`. (If your `sqlcmd` says `-C` is not recognised, remove ` -C` from both lines.)

## 3. Configure the application

Paste these five lines into the **same** PowerShell window, in the `ContractMGMT` folder. They last only while this window is open: if you close it, paste them again before step 4.

```powershell
$env:Ecp__Mode = "Test"
$env:Ecp__DemoSeed = "false"
$env:Ecp__Packs__AllowUnsignedSyntheticPacks = "true"
$env:Database__Provider = "SqlServer"
$env:Database__ConnectionString = "Server=localhost;Database=ecp_northwind;Integrated Security=True;TrustServerCertificate=True;Encrypt=True"
```

Now create the tables and ask for your one-time setup code (the first command compiles the application, so it takes a few minutes the first time):

```powershell
dotnet run --project src\Ecp.Host -c Release --no-launch-profile -- migrate
dotnet run --project src\Ecp.Host -c Release --no-launch-profile -- setup-token
```

You should see `Migrations applied.` and then a long code under *Setup token*. **Copy that code**; it is shown once, works once, and lasts 24 hours.

## 4. Run the application

```powershell
dotnet run --project src\Ecp.Host -c Release --no-launch-profile -- --urls http://localhost:5080
```

Wait for `Now listening on: http://localhost:5080`. **Leave this window open**; closing it stops the application. (To stop it, press Ctrl+C. To start it again later: open PowerShell, `cd $HOME\ContractMGMT`, paste the five lines from step 3, run the command above.)

## 5. Open the setup wizard

In your browser go to **<http://localhost:5080/Setup>**, paste the setup code, and click **Open setup**. Work down the list. Each step has one button (**Save and continue** unless it says otherwise).

## 6. Create Northwind Traders (the 14 steps)

| # | Step | What to do |
|---|---|---|
| 1 | Installation mode | Deployment: *Local development or evaluation*. Tenancy: *One organisation*. Tick the box saying this is for demonstration or testing only |
| 2 | Database | Click **Check database** |
| 3 | Document storage | Click **Run storage check** |
| 4 | Tenant | Tenant name: `Northwind Traders` |
| 5 | Solution pack | *Blank (no business defaults)* |
| 6 | Identity provider | Type: *Demonstration sign-in*. Provider key: `demo`. Name shown on the sign-in page: `Demonstration sign-in`. Leave the rest empty |
| 7 | Initial administrator | Full name: `Jeff`. Email: `jeff@northwind.example.test` (any valid-looking address). Leave **Also make this person an installation administrator** ticked |
| 8 | Organisation | Name: `Northwind Traders`. Country: *Australia (AU)*. Locale: `en-AU`. Time zone: Australia/Sydney. Currency: `AUD`. Reference prefix: `NWT`. Leave the tax identifier as *(none for now)*. (Use your own country, locale, zone and currency if different) |
| 9 | Branding | Product name: `Northwind Contract Register`. Keep the colour. Footer empty |
| 10 | Classification levels | Keep what is filled in |
| 11 | Email notifications | Provider: *Development capture (simulated; delivers nothing)*. From address: `northwind@northwind.example.test`. Send a test message to: `jeff@northwind.example.test`. You should see that the simulated provider accepted the test and that email is **Not Verified**: that is correct for a laptop |
| 12 | Demonstration data | Click **Skip this step** |
| 13 | Readiness review | Click **Run checks**. Two warnings appear (the database and platform checks, because no malware scanner exists; and email, because it is simulated). Type a short reason in each box, for example *Laptop trial* and *Laptop trial, email simulated*, then click **Run checks** again. It should say all blocking checks passed and the warnings were acknowledged |
| 14 | Finish setup | Click **Finish setup** |

You land on the sign-in page with "Setup is complete."

## 7. Verify the installation works

1. On the sign-in page there are two rows named *Jeff*. Click **Sign in** on the one in the **Northwind Traders** tenant (roles: Tenant Administrator, Organisation Administrator). The home page should show *Northwind Contract Register* and, under *Your access*, an Organisation Administrator role for **Northwind Traders**.
2. Click **Sign out**, then sign in again on the row marked **Installation** (System Administrator). Open **Health and readiness** from the menu.
   - *Overall: Degraded* is expected. The malware-scanner row (R-DOC-01) is Degraded because no real scanner exists, and the governance row says *TestSimplified*, which means you, one person, can publish your own changes.
   - Database, storage, time-zone, solution-pack and email rows should be **Healthy**. Anything **Unhealthy** is a problem.
3. Open <http://localhost:5080/health/live>. It should show `{"status":"Live"}`.

If all three work, Northwind Traders is installed.

---

## If something goes wrong

| What you see | What to do |
|---|---|
| `dotnet`, `git` or `sqlcmd` is "not recognised" | Close PowerShell, open it again, retry. If it persists, repeat that install |
| `Login failed` or a connection error in step 2 or 3 | The Basic install did not make your account a database administrator. Re-run the SQL Server installer's *Add current user* option, or ask IT |
| Step 3 says the database is missing | Do step 2 again; check the name is `ecp_northwind` in both places |
| The wizard says the token is wrong or used | Run the `setup-token` command again (with the five lines pasted first) and use the new code |
| The pack list in step 5 is empty | The line `$env:Ecp__Packs__AllowUnsignedSyntheticPacks = "true"` was not pasted before step 4. Stop the application (Ctrl+C), paste it, start again |
| The browser cannot reach `localhost:5080` | The window from step 4 was closed or shows an error; start it again |

**What this is not.** It is a Test-mode trial on one laptop with simulated email, no malware scanner and no real sign-in provider (anyone at this computer can choose a user). Production approval is external governance and nothing here declares it.
