﻿using AssetRipper.Assets;
using AssetRipper.Processing;

namespace AssetRipper.Export.PrimaryContent.Models;

public sealed class GlbPrefabModelExportCollection : MultipleExportCollection<PrefabHierarchyObject>
{
	public GlbPrefabModelExportCollection(GlbModelExporter assetExporter, PrefabHierarchyObject asset) : base(assetExporter, asset)
	{
		AddAssets(asset.Assets);
	}

	protected override string GetExportExtension(IUnityObjectBase asset) => "glb";
}
