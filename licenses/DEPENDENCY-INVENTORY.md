# Release dependency inventory

This inventory documents release-material provenance; it does not grant or infer a license for MishaWeb itself.

| Material | Included in release | Treatment |
|---|---:|---|
| Microsoft.Web.WebView2 1.0.4129.50 managed controls and x64 loader | Yes | The NuGet package's `LICENSE.txt` and `NOTICE.txt` are reproduced in `THIRD-PARTY-NOTICES.txt`. The separately serviced Evergreen runtime is a user prerequisite and is not bundled. |
| Public Suffix List snapshot retrieved 2026-08-01 | Yes | MPL-2.0 source and notice are recorded in `THIRD-PARTY-NOTICES.txt`, together with the exact SHA-256 and the location of the corresponding embedded source form. |
| .NET 10 Windows Desktop runtime | Self-contained release only | The release script copies the SDK distribution's `LICENSE.txt` and `ThirdPartyNotices.txt` into the verified payload. Framework-dependent releases do not redistribute the runtime. |
| EasyList, EasyPrivacy, uBlock Origin, and Brave filter sources | No cached upstream lists are packaged | Lists are fetched at runtime from the source URLs declared in `desktop/AdBlockEngine.cs`. The release contains only MishaWeb's local fallback/parser implementation, so upstream list text is not added to the binary payload. |
| Bunny logo and night-garden artwork under `desktop/Assets` | Yes | These are project-generated assets, not identified third-party inputs. This inventory does not assert a standalone license for them or for the project. |

When a dependency or bundled asset changes, update this inventory, the release notice, the NuGet lock files, and the release allowlist together.
