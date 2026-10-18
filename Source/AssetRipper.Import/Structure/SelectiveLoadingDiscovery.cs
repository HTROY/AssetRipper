using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Import.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_142;
using AssetRipper.SourceGenerated.Subclasses.AssetInfo;
using AssetRipper.SourceGenerated.Subclasses.PPtr_Object;

namespace AssetRipper.Import.Structure;

/// <summary>
/// A single entry of an <see cref="IAssetBundle.Container"/> table.
/// </summary>
/// <param name="AssetPath">The original asset path, e.g. <c>Assets/Resources_moved/ModelPrefabs/RU/RU_T14.prefab</c>.</param>
/// <param name="PathID">The path ID of the referenced object.</param>
/// <param name="TargetCollectionName">The collection that contains the referenced object, if it could be resolved.</param>
public sealed record DiscoveredContainerEntry(string AssetPath, long PathID, string? TargetCollectionName);

/// <summary>
/// The metadata of one serialized file collection, captured by a discovery pass.
/// </summary>
public sealed class DiscoveredCollection
{
	/// <summary>The name of the collection, which is the name of the serialized file.</summary>
	public required string Name { get; init; }

	/// <summary>The path of the game file that contains this collection.</summary>
	public required string FilePath { get; init; }

	/// <summary>
	/// The names of the dependency collections by serialized file external index.
	/// Index zero is the collection itself; a <see langword="null"/> entry is an unresolved dependency.
	/// </summary>
	public required IReadOnlyList<string?> DependencyNames { get; init; }

	/// <summary>The asset bundle container entries of this collection (original asset paths).</summary>
	public required IReadOnlyList<DiscoveredContainerEntry> ContainerEntries { get; init; }

	/// <summary>The class IDs of all objects in this collection (only available after discovery).</summary>
	public required IReadOnlyCollection<int> ClassIDs { get; init; }
}

/// <summary>
/// The result of a metadata-only discovery pass over a game.
/// </summary>
public sealed class DiscoveryResult
{
	public required IReadOnlyList<DiscoveredCollection> Collections { get; init; }

	public required int FailedFileCount { get; init; }

	public required string? ProjectVersion { get; init; }
}

/// <summary>
/// Reads the metadata of a game without materializing the assets, and returns the data that
/// selective loading needs: the game file of every collection, the collection dependency graph,
/// and the original asset paths of every asset bundle container.
/// </summary>
public static class SelectiveLoadingDiscovery
{
	/// <summary>
	/// Runs a metadata-only load of the given game paths.
	/// </summary>
	/// <remarks>
	/// The <see cref="ImportSettings.GameFilePathFilter"/> of <paramref name="configuration"/>
	/// is ignored by the discovery pass; all game files are inspected.
	/// </remarks>
	public static DiscoveryResult Discover(IReadOnlyList<string> paths, FileSystem fileSystem, CoreConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(configuration);

		bool previousMetadataOnly = SelectiveLoadingMode.MetadataOnly;
		int[] previousClassIDs = [.. SelectiveLoadingMode.MaterializedClassIDs];
		Func<string, bool>? previousFilter = configuration.ImportSettings.GameFilePathFilter;
		try
		{
			// Asset bundle objects (class 142) are the only assets that discovery needs: their
			// containers map the original asset paths to the collections that hold the objects.
			SelectiveLoadingMode.SetMaterializedClassIDs((int)ClassIDType.AssetBundle);
			SelectiveLoadingMode.MetadataOnly = true;
			configuration.ImportSettings.GameFilePathFilter = null;

			using GameStructure gameStructure = GameStructure.Load(paths, fileSystem, configuration);
			return CreateResult(gameStructure);
		}
		finally
		{
			configuration.ImportSettings.GameFilePathFilter = previousFilter;
			SelectiveLoadingMode.MetadataOnly = previousMetadataOnly;
			SelectiveLoadingMode.SetMaterializedClassIDs(previousClassIDs);
		}
	}

	private static DiscoveryResult CreateResult(GameStructure gameStructure)
	{
		List<DiscoveredCollection> collections = new();

		foreach (AssetCollection collection in gameStructure.FileCollection.FetchAssetCollections())
		{
			if (collection is not SerializedAssetCollection serializedCollection)
			{
				continue;
			}

			string?[] dependencyNames = new string?[serializedCollection.Dependencies.Count];
			if (dependencyNames.Length > 0)
			{
				dependencyNames[0] = serializedCollection.Name;
			}
			for (int i = 1; i < dependencyNames.Length; i++)
			{
				dependencyNames[i] = serializedCollection.Dependencies[i]?.Name;
			}

			List<DiscoveredContainerEntry> containerEntries = new();
			foreach (IUnityObjectBase asset in serializedCollection)
			{
				if (asset is not IAssetBundle assetBundle)
				{
					continue;
				}

				foreach (AccessPairBase<Utf8String, IAssetInfo> pair in assetBundle.Container)
				{
					IPPtr_Object pptr = pair.Value.Asset;
					string? targetCollectionName;
					if (pptr.FileID == 0)
					{
						targetCollectionName = serializedCollection.Name;
					}
					else if (pptr.FileID > 0 && pptr.FileID < dependencyNames.Length)
					{
						targetCollectionName = dependencyNames[pptr.FileID];
					}
					else
					{
						targetCollectionName = null;
					}
					containerEntries.Add(new DiscoveredContainerEntry(pair.Key.String, pptr.PathID, targetCollectionName));
				}
			}

			collections.Add(new DiscoveredCollection()
			{
				Name = serializedCollection.Name,
				FilePath = serializedCollection.FilePath,
				DependencyNames = dependencyNames,
				ContainerEntries = containerEntries,
				ClassIDs = serializedCollection.DiscoveryClassIDs ?? Array.Empty<int>(),
			});
		}

		return new DiscoveryResult()
		{
			Collections = collections,
			FailedFileCount = CountFailedFiles(gameStructure.FileCollection),
			ProjectVersion = gameStructure.FileCollection.GetMaxUnityVersion().ToString(),
		};

		static int CountFailedFiles(Bundle bundle)
		{
			int count = bundle.FailedFiles.Count;
			foreach (Bundle child in bundle.Bundles)
			{
				count += CountFailedFiles(child);
			}
			return count;
		}
	}
}
