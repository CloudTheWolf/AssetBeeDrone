# AssetBee Drone

AssetBee Drone is a .NET 10 background service that inventories Windows, Linux,
and macOS devices and posts a versioned JSON document to an HTTPS endpoint.

## Collected data

- Device name, firmware serial number, manufacturer, and model (system SKU)
- Operating system name, version, display version, build, and kernel
- Installed and available OS/security updates
- CPU model/core counts, total memory, and block devices (Linux reports
  partition names such as `nvme0n1p1`, excluding loop devices and virtual
  filesystems like tmpfs/overlay/squashfs)
- BitLocker, LUKS/dm-crypt, or FileVault state
- Windows BitLocker numerical recovery passwords and their key protector IDs
- AD/Entra/workplace, realmd/SSSD, Apple AD, and MDM workspace state
- Built-in login systems plus GCPW, Jamf Connect, XCreds, NoMAD, SSSD, and LDAP
- Windows Security Center products, Gatekeeper/XProtect, and recognized
  Linux/macOS AV or EDR products
- CycloneDX-style SBOM of host packages (dpkg/rpm, Windows Uninstall registry
  and AppX, macOS pkgutil), plus running Docker container package inventories
  on Linux

Each section has a `status` (`available`, `unavailable`, `unsupported`,
`accessDenied`, or `error`), a `value`, and an optional safe `detail`. A failed
probe does not suppress the rest of the report.

## Configuration

Configure `appsettings.json` or use environment variables with double
underscores:

```text
Drone__Endpoint=https://inventory.example.com/api/v1/inventory
Drone__CollectionIntervalMinutes=360
Drone__RequestTimeoutSeconds=30
Drone__MaxRetryAttempts=3
Drone__BearerToken=secret
Drone__Type=
Drone__Debug=false
Drone__DebugOutputPath=inventory-debug.json
Drone__IncludeSbom=true
Drone__IncludeContainerSboms=true
```

`Drone__ApiKey` may be used instead of `Drone__BearerToken`; it is sent as
`X-Api-Key`. Endpoints must use HTTPS. Plain HTTP is accepted only when debug
mode is enabled and the endpoint is loopback (`localhost`, `127.0.0.1`, or
`::1`). The service posts once at startup and then at the configured interval.
Transient HTTP failures and status codes 408, 429, and 5xx are retried with
exponential backoff.

`Drone__Type` optionally overrides asset classification with `hardware` or
`virtualware`. When omitted, Drone checks platform virtualization signals.
Hardware assets also include `hardwareType` when the chassis can be identified:
`laptop`, `desktop`, or `server`.

`Drone__IncludeSbom` (default `true`) generates a CycloneDX-style SBOM for host
OS packages. On Linux, `Drone__IncludeContainerSboms` (default `true`) also
inventories packages inside running Docker containers via `docker ps` and
`docker exec` (dpkg, rpm, or apk). Container SBOMs require the Docker CLI and
permission to inspect/exec into containers.

### Debug payload dump

Set `Drone:Debug` to `true` (or pass `--debug`) to write the exact outbound JSON
body to `Drone:DebugOutputPath` before each HTTPS post. Relative paths resolve
from the process working directory. This dump intentionally includes BitLocker
recovery keys and other secrets when present—use only on trusted machines and
delete the file when finished:

```sh
./AssetBee.Drone --debug
# or
Drone__Debug=true Drone__DebugOutputPath=./debug/last-push.json ./AssetBee.Drone
```

The endpoint receives `application/json` with schema version `1.0`. The root
fields are `schemaVersion`, `collectedAtUtc`, `platform`, `type`,
`hardwareType`, `deviceName`, `serialNumber`, `manufacturer`, `model`,
`operatingSystem`, `cpu`, `memory`, `disks`, `diskEncryption`,
`domainWorkspace`, `loginProviders`, `antivirus`, `updates`, and `sbom`.

`manufacturer` comes from the system manufacturer and `model` from the system
SKU (falling back to the product/model name when SKU is blank). Both values are
normalized to camelCase with underscores and similar separators removed
(for example `Dell Inc.` -> `dellInc`, `Latitude_5520` -> `latitude5520`).

Validate payloads against [`schema.json`](schema.json) (JSON Schema draft
2020-12).

## BitLocker recovery-key security

On Windows the service intentionally runs as LocalSystem and sends full
numerical recovery passwords. Recovery passwords exist only in the in-memory
inventory object and outbound HTTPS body unless `Drone:Debug` is enabled, which
writes the full payload (including recovery keys) to disk for troubleshooting.
Endpoint credentials and `appsettings.json` must be restricted to
administrators/SYSTEM. The receiver should apply equivalent secret-storage,
audit, rotation, and access-control policies. If key access is denied or no
recovery protector exists, the report contains no key rather than failing.

## Build and test

Install the .NET 10 SDK, then run:

```sh
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

Native AOT publishing must be performed on the target operating-system family:

```sh
dotnet publish -p:PublishProfile=linux-x64
dotnet publish -p:PublishProfile=linux-arm64
dotnet publish -p:PublishProfile=win-x64
dotnet publish -p:PublishProfile=osx-x64
dotnet publish -p:PublishProfile=osx-arm64
```

## Packaging

Package versions are computed automatically (override with `VERSION`):

1. `VERSION` environment variable, else
2. Git tag `v*` (CI tag builds / exact tag on HEAD), else
3. `1.0.{GITHUB_RUN_NUMBER}` on GitHub Actions, else
4. `1.0.{git rev-list --count HEAD}` locally, else
5. `1.0.0`

Pushing to `main` runs `.github/workflows/release-packages.yml`, which publishes
Native AOT builds and uploads packages as workflow artifacts. Pushing a `v*` tag
also creates a GitHub Release from those artifacts.

Native AOT publish and native package builds must run on the matching OS family
(or via that workflow). After publishing a RID:

```sh
# Linux / macOS host
./deploy/packaging/build.sh --rid linux-x64 --publish-dir bin/Release/net10.0/linux-x64/publish

# Windows host (PowerShell)
.\deploy\packaging\build-archive.ps1 -PublishDirectory .\bin\Release\net10.0\win-x64\publish -Rid win-x64
.\deploy\windows\build-msi.ps1 -PublishDirectory .\bin\Release\net10.0\win-x64\publish
```

Artifacts land in `dist/`: portable archives (`.zip` / `.tar.gz`) plus native
packages (`.msi`, `.deb`, `.rpm`, `.pkg`). Packages are unsigned in v1; expect
SmartScreen / Gatekeeper prompts until signed and notarized.

## Install

Pass an HTTPS `Endpoint` and exactly one of `BearerToken` / `ApiKey` at install
time.

### Windows

**MSI**

Interactive (no properties required): double-click the MSI or run `msiexec /i AssetBee.Drone-<version>-win-x64.msi`. The wizard prompts for endpoint and bearer token or API key. On upgrade or reinstall, it reads the existing `appsettings.json` (when present) and pre-fills those fields; you can confirm or change them before continuing.

Silent first install:

```powershell
msiexec /i AssetBee.Drone-<version>-win-x64.msi /qn `
  ENDPOINT=https://inventory.example.com/api/v1/inventory `
  BEARERTOKEN=secret
# or APIKEY=secret
```

Silent upgrade / reinstall: run the new MSI without `ENDPOINT` / auth properties to keep the existing settings (loaded from disk/registry). Pass those properties only when you want to change the connection settings.

The MSI also installs **AssetBee.Drone.Tray**, a notification-area app that starts at logon. Right-click the tray icon to see the last successful sync time and choose **Sync Now**. Exit closes only the tray helper; the `AssetBeeDrone` service keeps running. The tray talks to the service through `%ProgramData%\AssetBee\Drone\` (status heartbeat + sync request files).

**Portable archive / publish folder**

```powershell
.\deploy\windows\install.ps1 `
  -PublishDirectory .\bin\Release\net10.0\win-x64\publish `
  -Endpoint https://inventory.example.com/api/v1/inventory `
  -BearerToken 'secret'
```

Registers `AssetBeeDrone` as an automatic LocalSystem service, installs the tray
helper (HKLM Run), and locks secrets in `appsettings.json` to SYSTEM/Administrators
while allowing Users to run the tray binary. Uninstall with
`deploy/windows/uninstall.ps1` or Add/Remove Programs for the MSI.

### Linux

**Package (.deb / .rpm)**

```sh
sudo deploy/linux/packaging/install-package.sh \
  dist/AssetBee.Drone-<version>-linux-x64.deb \
  --endpoint https://inventory.example.com/api/v1/inventory \
  --bearer-token secret
```

**Portable archive / publish folder**

```sh
sudo deploy/linux/install.sh bin/Release/net10.0/linux-x64/publish \
  --endpoint https://inventory.example.com/api/v1/inventory \
  --bearer-token secret
sudo journalctl -u assetbee-drone
```

Uninstall:

```sh
sudo deploy/linux/uninstall.sh
# or: sudo dpkg -r assetbee-drone / sudo rpm -e assetbee-drone
```

### macOS

**Package (.pkg)**

```sh
sudo deploy/macos/packaging/install-package.sh \
  dist/AssetBee.Drone-<version>-osx-arm64.pkg \
  --endpoint https://inventory.example.com/api/v1/inventory \
  --bearer-token secret
```

**Portable archive / publish folder**

```sh
sudo deploy/macos/install.sh bin/Release/net10.0/osx-arm64/publish \
  --endpoint https://inventory.example.com/api/v1/inventory \
  --bearer-token secret
```

You can still pass `--settings-file path.json` instead of endpoint/auth flags.
Uninstall:

```sh
sudo deploy/macos/uninstall.sh
```

## Platform notes

- Hardware and security probes use OS-standard files and command-line APIs with
  bounded timeouts and no shell interpolation.
- Windows Security Center inventory is normally available on client Windows,
  not Windows Server. BitLocker requires the BitLocker PowerShell module and an
  elevated service account.
- Linux has no universal antivirus registry. The initial detector recognizes
  ClamAV, Defender for Endpoint, CrowdStrike, SentinelOne, and Sophos systemd
  services and can be extended in the collector.
- FileVault reports startup-volume state. macOS third-party products are
  identified from standard application locations.
- Some VM/container firmware exposes blank or placeholder serial numbers.
