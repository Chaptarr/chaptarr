# Administration

## Access and authentication

The package grants the app URL to YunoHost administrators by default. Change the `main` permission in YunoHost if other groups should use it. YunoHost SSO headers are not passed to ChaptarrNG; configure the app's own authentication after installation if you need per-user access controls.

## Media access

The package adds the ChaptarrNG system user to YunoHost's `multimedia` group. Configure audiobook folders in ChaptarrNG and make sure those folders grant suitable group access. The app needs write permission for files it converts or edits.

## Service and logs

The systemd service is named after the app instance. View its status and journal from YunoHost or run:

```bash
sudo yunohost service status chaptarrng
sudo yunohost service log chaptarrng
sudo journalctl -u chaptarrng
```

## Updates and backups

Use YunoHost to back up, restore, and upgrade ChaptarrNG. The package downloads architecture-specific bundles from the upstream GitHub release and verifies each archive against its pinned SHA-256 checksum. YunoHost installs and patches the .NET 10 ASP.NET Core runtime from Microsoft's Debian package feed. ChaptarrNG's built-in updater is set to external mode so app updates remain managed by YunoHost.
