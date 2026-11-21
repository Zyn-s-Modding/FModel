using System.Numerics;
using CUE4Parse.GameTypes.Nascar.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Houdini;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Paper2D;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using Editor;
using Editor.Managers;
using Snooper.Hosting;
using Snooper.Rendering.Actors;
using Snooper.Rendering.Actors.Custom;
using Snooper.Rendering.Components.Camera;
using Snooper.Rendering.Components.Mesh;
using Snooper.Rendering.Components.Primitive;
using Snooper.Rendering.Components.Transforms;
using Snooper.Rendering.Systems;
using Snooper.UI;

namespace FModel.Desktop;

internal static class SnooperScene
{
    public static bool ShouldPreview(UObject asset, DesktopSettings settings) => asset switch
    {
        UStaticMesh or UIRMesh or UHoudiniStaticMesh or UGeometryCollection => settings.PreviewStaticMeshes,
        USkinnedAsset => settings.PreviewSkeletalMeshes,
        UAnimationAsset => settings.PreviewAnimations,
        USkeleton => settings.SaveSkeletonAsMesh && settings.PreviewSkeletalMeshes,
        UMaterialInterface => settings.PreviewMaterials,
        UWorld or UBlueprintGeneratedClass => settings.PreviewWorlds,
        UPaperTileMap { Game: EGame.GAME_DeadByDaylight } => settings.PreviewWorlds,
        _ => false
    };

    public static bool CanView(UObject asset) => asset is UStaticMesh or UIRMesh or UHoudiniStaticMesh or USkinnedAsset
        or UGeometryCollection or UAnimationAsset or USkeleton or UMaterialInterface or UBlueprintGeneratedClass or UWorld
        or UPaperTileMap { Game: EGame.GAME_DeadByDaylight };

    public static Actor Create(UObject asset, DesktopSettings settings, GameProfile profile, CancellationToken token, IProgress<string>? progress = null)
    {
        Bridge.Options = new SnooperOptions(asset is UWorld or UPaperTileMap ? ENaniteMeshFormat.NoNanite : settings.NaniteFormat,
            materialDepth: settings.MaterialDepth, texturePlatform: profile.TexturePlatform, loadMorphTargets: settings.ExportMorphTargets,
            socketFormat: settings.SocketFormat, includeDisabledMeshSections: settings.IncludeDisabledMeshSections,
            maxTextureMipSize: settings.PreviewMaxTextureSize);
        return asset switch
        {
            UStaticMesh mesh => new MeshActor(mesh),
            UIRMesh mesh => new MeshActor(mesh),
            UHoudiniStaticMesh mesh => new MeshActor(mesh),
            USkinnedAsset mesh => new MeshActor(mesh),
            UGeometryCollection geometry => new MeshActor(geometry),
            UAnimationAsset animation => new MeshActor(animation),
            USkeleton skeleton => new MeshActor(skeleton),
            UMaterialInterface material => new MaterialActor(material),
            UBlueprintGeneratedClass blueprint => new BlueprintActor(blueprint),
            UWorld world => new WorldActor(world, progress, token),
            UPaperTileMap { Game: EGame.GAME_DeadByDaylight } map => new DeadByDaylightMapActor(map, progress, ct: token),
            _ => throw new NotSupportedException($"Snooper cannot display {asset.ExportType}.")
        };
    }

    public static void Load(EditorWindow editor, Actor? actor)
    {
        editor.Invoke(() =>
        {
            if (editor.Manager.RootActor is null)
            {
                var scene = new Actor("Scene");
                scene.Components.Add(new BoxComponent(Vector3.Zero, Vector3.One));
                editor.Manager.LoadScene(scene);
                editor.Manager.LoadScene(new SunActor());
                editor.Manager.LoadScene(new EnvironmentActor());
                editor.Manager.LoadScene(new GridActor(actor is null or MeshActor));
                editor.Manager.LoadScene(new CameraActor(actor is WorldActor or DeadByDaylightMapActor ? 10 : actor is BlueprintActor ? 5 : 1));
            }
            WindowRequests.Request(actor is MaterialActor ? Snooper.Settings.MaterialInspectorWindow : Snooper.Settings.ViewportWindow);
            if (actor is not null) editor.Manager.LoadScene(actor);
            editor.Show();
            if (actor is DeadByDaylightMapActor map)
            {
                if (editor.Manager is EditorManager manager) manager.DeadByDaylightMap = map;
                if (editor.Manager.GetSystem<CameraSystem>()?.Active is InteractiveCameraComponent camera)
                {
                    camera.SetLocalTransform(new Transform(Vector3.Zero, new Vector3(135, 50, 0)));
                    camera.TeleportTo(Vector3.Zero, map.OverviewDistance);
                }
            }
            if (actor?.RootComponent is MeshComponent mesh)
            {
                if (mesh.SnapToGround()) mesh.UpdateWorldMatrix();
                mesh.TeleportTo();
                if (editor.Manager is InterfaceManager manager) manager.SelectComponent(mesh);
            }
        });
    }
}
