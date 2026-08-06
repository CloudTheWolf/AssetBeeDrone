# AssetBee Drone

AssetBee Drone is a .NET 10 background service that inventories Windows, Linux,
and macOS devices and posts a versioned JSON document to an HTTPS endpoint.

## Collected data

- Device name, firmware serial number, manufacturer, and model (system SKU)
- Operating system name, version, display version, build, and kernel
- Installed and available OS/security updates
- CPU model/core counts, total memory, and mounted disk capacity/free space
- BitLocker, LUKS/dm-crypt, or FileVault state
- Windows BitLocker numerical recovery passwords and their key protector IDs
- AD/Entra/workplace, realmd/SSSD, Apple AD, and MDM workspace state
- Built-in login systems plus GCPW, Jamf Connect, XCreds, NoMAD, SSSD, and LDAP
- Windows Security Center products, Gatekeeper/XProtect, and recognized
  Linux/macOS AV or EDR products

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
`domainWorkspace`, `loginProviders`, `antivirus`, and `updates`.

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

## Install

### Windows

From elevated PowerShell, publish `win-x64` and run:

```powershell
.\deploy\windows\install.ps1 `
  -PublishDirectory .\bin\Release\net10.0\win-x64\publish `
  -Endpoint https://inventory.example.com/api/v1/inventory `
  -BearerToken 'secret'
```

The installer registers `AssetBeeDrone` as an automatic LocalSystem service
and locks its directory ACL. Use `deploy/windows/uninstall.ps1` to remove it.

### Linux

Edit `/etc/assetbee-drone/environment` after installation and keep it mode
`0600`:

```sh
sudo deploy/linux/install.sh bin/Release/net10.0/linux-x64/publish
sudo systemctl restart assetbee-drone
sudo journalctl -u assetbee-drone
```

To uninstall:

```sh
sudo systemctl disable --now assetbee-drone
sudo rm /etc/systemd/system/assetbee-drone.service
sudo systemctl daemon-reload
sudo rm -rf /opt/assetbee-drone /etc/assetbee-drone
```

### macOS

Create a root-readable `appsettings.production.json` with the `Drone` section,
then run:

```sh
sudo deploy/macos/install.sh \
  bin/Release/net10.0/osx-arm64/publish \
  appsettings.production.json
```

To uninstall:

```sh
sudo launchctl bootout system/com.assetbee.drone
sudo rm /Library/LaunchDaemons/com.assetbee.drone.plist
sudo rm -rf /Library/AssetBee/Drone
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
