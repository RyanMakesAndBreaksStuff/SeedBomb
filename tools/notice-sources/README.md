# Pinned third-party license sources

These files are reviewed originals for packages that do not include license
text in the NuGet package. `Generate-ThirdPartyNotices.ps1` never downloads
terms at runtime.

When a package version changes and still has no license file:

1. Identify the nuspec repository URL and commit for that exact version.
2. Download the license from that commit (or the vendor legal URL).
3. Record SHA-256 and URL in `tools/ThirdPartyNoticeOverrides.json`.
4. Place the bytes in this folder.
5. Run `pwsh -NoProfile -File tools/Generate-ThirdPartyNotices.ps1` then `-Verify`.

Do not label Microsoft Dynamics 365 SDK or Windows SDK terms as MIT.
GET is required for some Microsoft legal URLs; HEAD may return 403.
