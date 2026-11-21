# macOS port

The macOS application uses Avalonia for FModel and the original ImGui interface for Snooper. FModel's views follow its Adonis theme colors, compact controls, archive/folder/package tabs, thumbnail explorer, properties/image split and export workflows. Visual references include the [official FModel wiki](https://github.com/4sval/FModel/wiki/Getting-Started) and [Snooper screenshots](https://github.com/FModel/Snooper#screenshots). The compatibility table below records implemented workflows and remaining gaps.

## Build and run

Install the .NET 10 SDK, the Xcode command line tools, and `uv`. The native builds download pinned tools and dependencies into `.native-deps/`. Initialize submodules if needed.

```sh
git submodule update --init --recursive
dotnet build FModel/FModel.slnx
dotnet run --project FModel/FModel.csproj
```

The macOS target uses `net10.0` and the current CPU architecture. The build compiles the native ACL animation decoder and the AVFoundation audio bridge with Clang. It also builds pinned vgmstream and FFmpeg audio decoders with MPEG, Vorbis, CELT, Speex, ATRAC9, G.719 and G.722.1 support, plus the original native unluac decompiler. Build downloads are checksum-verified and cached under `.native-deps/`. Bundled helpers use relative library paths and macOS system frameworks.

Open a directory directly with the following command.

```sh
dotnet run --project FModel/FModel.csproj -- /path/to/game/Content/Paks
```

Choose the correct Unreal/game version in Directory → Selector. Directory → AES provides a main-key field and per-archive keys, with Refresh for a configured endpoint. Settings → General selects a local `.usmap`, edits AES/mapping endpoints and game-version overrides. Local and remote mapping modes are selectable. F12 reloads the selected mapping source. AES reload supports Always, Never and Once Per Day, with saved-key recovery when an endpoint is unavailable. Right-click packages or folders for the original extraction, metadata, references, export and copy actions. Ordinary extraction replaces the selected document. Extract in New Tab opens each selected package and reuses a blank tab. Properties export writes one JSON array for the complete package. Properties, audio, code and creator images save directly into their selected output folders. Search results support Go To and double-click navigation into the package browser. Run queued exports in Views → Export Session, where individual items can be removed and export options overridden for the session. Export Immediately runs queued work automatically. Convert Audio on Bulk Export preserves the original optional PCM conversion. Customize Output Folders exposes the original raw, properties, texture and audio locations. Keep Directory Structure controls flat output for the direct exporters and raw files.

The selector accepts installation roots and locates a single project's `Content/Paks` directory. A local `Engine/Build/Build.version` supplies a version hint. The tested Fortnite 24.20 intermediate format has its own preset. Advanced custom-version and boolean overrides are editable in Settings. Launch a complete local profile with `--profile /path/to/profile.json`. Relative mappings paths in a profile resolve beside the profile file.

## App bundle

```sh
bash scripts/package-macos.sh osx-arm64
# Intel build, made with the same source:
bash scripts/package-macos.sh osx-x64
```

The script creates `dist/<runtime>/FModel.app`, including .NET, native libraries, the original application icon and licenses. It stages the bundle, signs it locally, and preserves an existing bundle as `FModel.previous-<timestamp>.app`. Open the resulting application from Finder. Distribution requires Developer ID signing and notarization.

The deployment target is macOS 15+. Execution has been checked on an Apple M4 running macOS 27.2. Intel native renderer checks have also run under Rosetta. Intel hardware and older macOS versions need hardware validation.

## Compatibility

| Workflow | macOS status |
| --- | --- |
| Compile the complete solution | Verified |
| Pak and IoStore mounting, loose files and encrypted archives | Implemented, with UE5.8/UE6.0 fixtures exercised |
| Manual game/version selection, AES and local mappings | Implemented |
| Archive, folder and package browsing, category filters, search and references | Implemented with modeless search, Go To selection, original context actions, current/new-tab extraction semantics and per-folder filters |
| JSON, plain text, binary configuration, shader archives, metadata, localization and asset registry previews | Implemented. Original large-package focus, export-range titles and object-reference selection are reused |
| Blueprint pseudo-code and IoStore reference search | Implemented using CUE4Parse |
| Texture, array, Paper2D sprite/flipbook and SVG previews, image popouts, magnifier, copying and saving | Implemented with original image context commands, animated previews, vector preservation and alpha-preview toggle. PNG/JPEG/TGA/WebP texture exports reuse CUE4Parse |
| Audio playlist, playback, seeking, volume, devices, spectrum and saving | Original player controls use AVFoundation, CoreAudio and a native FFT. Vorbis, HCA/ADX and bundled vgmstream/FFmpeg codecs convert to PCM for playback |
| Mesh/material/world/animation export | Uses CUE4Parse's existing exporters and format support |
| Raw package and payload export | Implemented, with validation of output paths and preservation of dotted names |
| 3D preview | Original Snooper actor/component renderer and editor, hosted on the Cocoa main thread |
| Export sessions, progress, cancellation, backups, image merger | Implemented, including session overrides, Make Default, queue removal/re-add and automatic export |
| Settings, logs, Finder output-folder integration, drag and drop | Implemented |
| Snooper materials, lighting, animation timeline, skeleton and morph panels, bone edits, streamed levels and scene export | Original panels and render systems reused. Real Fortnite animation playback, wireframe, bone edits/reset and scene-export queuing exercised. Native window tests are blocked while the desktop is locked. A real seven-morph head mesh has been identified. Its deformation check awaits an unlocked display |
| Fortnite/Valorant live manifests and remote key/mapping endpoints | Implemented from the original manifest and endpoint flows. Cached Valorant stream boundary reads and AES expression parsing are tested. Public key/mapping endpoint schemas and Fortnite Studio manifest metadata respond. The original Valorant LIVE manifest URL returns HTTP 404. Full Fortnite live mounting remains unverified |
| Automatic installation detection | Saved games, macOS Steam/Epic manifests and Unreal app bundles are supported. Installation-root detection remains available |
| Creator/cosmetic images | Original Skia creators, resources, styles and game fonts reused. Fortnite Recruit rendering and image export verified |
| Wwise/FMOD/CRI and game-specific bank/event browsing | Original provider extraction reused, including Borderlands, Gotham Knights, Supermassive and Square Enix handlers. Wwise bank preview/extraction/export is exercised. Other game-specific handlers need representative archives for validation |
| Crunch and Switch Tegra native texture codecs | Bundled portable decoders. Real CRN DXT1/DXT5 samples and upstream Tegra RGBA, BC7 and 3D golden vectors exercised |
| Native Lua decompiler/disassembler | Original unluac built for both architectures. Lua 5.1 decompile/disassemble checks pass |
| Game-specific Windows-only crypto/audio helpers | Native helper parity remains incomplete for the ProSpi helper. RADA codec variants still need matching sample data for decoder validation |
| Discord Rich Presence | Original managed service and Always/Never setting reused. Connection to a running Discord desktop client remains unverified |
| Windows auto-update integration | Windows updater remains excluded. macOS release signing, notarization and an update feed need release infrastructure |
| AvalonEdit syntax highlighting, folding, search, inline asset links and thumbnail explorer | AvaloniaEdit port using the original syntax files, plus virtualized original-size thumbnail cards |

The parser/export library's existing per-game limitations apply. Unsupported operations report errors. Snooper uses Mesa LLVMpipe software rendering to supply the compute, SSBO, DSA and bindless operations its existing shaders require. Apple's OpenGL context presents the completed framebuffer. This consumes CPU time and runs substantially slower than the Windows GPU renderer. Larger scenes can be slow.

FModel and Snooper use Segoe UI in the Windows source. The macOS bundle includes Microsoft's [Selawik](https://github.com/microsoft/Selawik) replacement in regular, bold and semibold weights, licensed under SIL OFL 1.1. Supplied `segoeui.ttf`, `segoeuib.ttf` and `seguisb.ttf` files in `~/Library/Application Support/FModel/Fonts` are used for the original typeface. `consola.ttf` enables the original document font. `FMODEL_FONT_DIRECTORY` can select another font directory. Fonts are registered for the application process. Snooper builds its atlas at the Retina pixel density.

## Validation

```sh
dotnet run --project FModel/FModel.csproj -- --self-test "$PWD"
dotnet run --project FModel/FModel.csproj -- --ui-smoke-test
# In-memory Skia UI checks, including on a locked Mac
dotnet run --project FModel/FModel.csproj -- --ui-smoke-test /path/to/profile.json --headless
```

The integration checks use the checked-in UE5.8 and UE6.0 fixtures. They cover archive loading, mappings, encrypted Oodle archives, package JSON, localization, textures, native audio decoding/seeking, raw packages and PNG/glTF/audio exports. Native ACL loading and its aligned-allocation ABI are checked. Animation conversion is attempted, but these fixtures omit their imported engine compression codec: that check explicitly reports a skip and verifies the exporter surfaces the failure. Separately, [real Fortnite 24.20 assets](fortnite24.20-validation.md) have exercised animation export, static and skeletal meshes, textures and Bink playback. The fixture checks write exports to a temporary directory and remove it on completion. `--ui-smoke-test` opens a real native window, renders a screenshot and exits. The `--headless` variant uses the same Avalonia views with an in-memory Skia renderer. It checks package visibility after Go To, document replacement/new-tab behavior, playlists, animation lifetime, direct exports, custom folders, queue removal and session output/format overrides.

To inspect the viewport, retain integration exports in a new temporary directory and render an exported GLB.

```sh
dotnet run --project FModel/FModel.csproj -- --self-test "$PWD" /tmp/fmodel-fixtures
dotnet run --project FModel/FModel.csproj -- --viewer-smoke-test \
  /tmp/fmodel-fixtures/UE6_0/CUE4ParseFixtures/Content/Fixtures/Meshes/SM_Fixture.glb
```

The viewport check reads GPU pixels and verifies that geometry was drawn. The UI screenshot and viewport screenshot are saved in the application data directory.

## Files and submodules

Submodule changes are confined to **Snooper**. They cover macOS context presentation, main-thread hosting, Retina rendering, fonts, wireframe compatibility and editor lifecycle fixes. CUE4Parse source changes are generated under `obj/` by root build targets. These adapters handle native texture libraries, borrowed Crunch buffer lifetime, texture errors and the opt-in `Nanite.HasNormalPrecision` layout switch needed for transitional builds such as Fortnite 24.20. Original package-loading and pagination logic is reused through a generated adapter. An export-session adapter exposes queue snapshots and preserves exporter identity when a removed package is re-added, avoiding duplicate output writes. The adapters check the upstream source shape before generating files.

Bundled decoder licenses are included as `vgmstream-COPYING.txt`, `FFmpeg-LICENSE.txt`, dependency license files and `Unluac-LICENSES.txt`. FFmpeg 8.0.1 uses a shared LGPL audio-only configuration. Its source URL, checksum and complete build options are recorded in `scripts/build-macos-ffmpeg.sh`. `FMODEL_VGMSTREAM` can select another compatible decoder. Archive-specific encryption and codec variants still need matching game data for validation.

The Mac must have an unlocked, active display for Avalonia and Snooper window tests. File-based builds, integration checks and the optional headless Avalonia/Skia UI checks can continue while locked. Interactive validation resumes after unlocking and activating a display.

The `.gitmodules` configuration points Snooper to the user's fork.

Settings and logs use `~/Library/Application Support/FModel`. Exports default to `~/FModel/Output`. `FMODEL_DATA_DIR` overrides application data storage for validation or isolated profiles. The native port stores settings in `desktop-settings.json`.

## Architecture references

The UI migration follows [Avalonia's WPF migration model](https://docs.avaloniaui.net/docs/migration/wpf). Snooper uses its existing ImGui panels and render pipeline through the bundled software context. The earlier GLB viewport remains available to the diagnostic CLI. See [.NET 10 platform support](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) for the runtime's platform lifecycle.
