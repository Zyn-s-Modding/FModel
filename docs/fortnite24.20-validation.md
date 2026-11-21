# Fortnite 24.20 validation

Test source: the provided 24.20 installation's `FortniteGame/Content/Paks` directory. Installation metadata reports `++Fortnite+Release-24.20`, CL-25156858, UE5.1. The directory contains both Windows client archives and UEFN archives.

The initial generic UE5.1 profile could read properties and some skeletal/animation data, but failed on textures, static meshes and audio. A generic UE5.2 profile fixed textures/audio while breaking skeletal mesh weights. The verified transitional profile is:

| Setting | Value |
| --- | --- |
| Base engine | `GAME_UE5_2` |
| `FUE5MainStreamObjectVersion` (`697DD581E64F41ABAA4A51ECBEB7B628`) | `88` |
| `FFortniteMainBranchObjectVersion` (`601D1886AC644F84AA16D3DE0DEAC7D6`) | `80` |
| `Nanite.HasNormalPrecision` | `false` |

Choosing this installation root now detects that profile. The Nanite option defaults to upstream behavior for all other profiles and is implemented by a generated source override, not by modifying CUE4Parse's submodule.

Public validation inputs came from the [24.20 AES archive](https://github.com/dippyshere/fortnite-aes-archive/blob/master/archive/dynamic/24.20.md) and [FortForge's mapping archive](https://www.fortforge.co.uk/mappings). The available mapping was the Android CL-24939793 schema for 24.20, **not an exact Windows CL-25156858 schema**. The following results establish the tested cases; they do not certify every class/layout in the build.

The validation mounted **52 archives and indexed 675,866 files**. Six readers remained unloaded: one metadata/auxiliary reader and five readers associated with missing key GUID `887E952B9327245302EAD1F32AA9EE6C`. Content encrypted with that key remains unavailable until the user supplies it.

| Sample | Verified result |
| --- | --- |
| `CID_001_Athena_Commando_F_Default` | Cosmetic properties to JSON |
| `T_Banners-Icons-S9-Completion` | Texture preview pixels and PNG export |
| `SM_ConstructorShield_Plate` | Static mesh to binary glTF |
| `SK_Constructor_Decoy` | Skeletal mesh to binary glTF |
| `Chashu_PropAnim_M` | Montage/sequences to UE animation exports |
| `VR_click1` | Bink payload extraction, PCM conversion, native playback/pause/seeking; duration 0.67925 seconds |

Mesh validation disables material dependency export so it tests geometry without exporting an uncontrolled portion of the game. Full game worlds, all material variants, every animation codec, and the remaining Snooper editor features are not certified by these samples.

The local `dist/validation-fortnite24.20/` artifact contains an importable `profile.json`, its mapping file, sample exports and `report/validation.json`. These are ignored build/validation outputs, not committed game data. Import the profile in Directory → Selector or pass `--profile` at launch.

Reproduce the headless sample check from the repository root:

```sh
dotnet run --project FModel/FModel.csproj -- --validate-game \
  dist/validation-fortnite24.20/profile.json dist/validation-fortnite24.20/report \
  CID_001_Athena_Commando_F_Default.uasset T_Banners-Icons-S9-Completion.uasset \
  SM_ConstructorShield_Plate.uasset SK_Constructor_Decoy.uasset \
  Chashu_PropAnim_M.uasset VR_click1.uasset
```

This check writes reports/exports outside the source archives. Its audio check plays silently at zero volume, verifies time advances, then pauses, seeks and stops.
