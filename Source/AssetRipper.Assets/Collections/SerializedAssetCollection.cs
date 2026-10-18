using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;

namespace AssetRipper.Assets.Collections;

/// <summary>
/// A collection of assets read from a <see cref="SerializedFile"/>.
/// </summary>
public sealed class SerializedAssetCollection : AssetCollection
{
	private FileIdentifier[]? DependencyIdentifiers { get; set; }

	/// <summary>
	/// The raw dependency identifiers of the serialized file this collection was created from.
	/// This is only captured in metadata-only (discovery) mode, and it is <see langword="null"/>
	/// otherwise.
	/// </summary>
	public IReadOnlyList<FileIdentifier>? DiscoveryDependencyIdentifiers { get; private set; }

	/// <summary>
	/// The dependency collection names by serialized file external index. Index zero is the
	/// collection itself; a <see langword="null"/> entry is an unresolved or non-collection
	/// dependency. This is <see langword="null"/> until the dependency list has been initialized.
	/// </summary>
	public IReadOnlyList<string?>? DependencyNames { get; private set; }

	/// <summary>
	/// The class IDs of all objects in this collection. This is only captured in metadata-only
	/// (discovery) mode, and it is <see langword="null"/> otherwise.
	/// </summary>
	public IReadOnlyCollection<int>? DiscoveryClassIDs { get; private set; }

	private SerializedAssetCollection(Bundle bundle) : base(bundle)
	{
	}

	internal void InitializeDependencyList(IDependencyProvider? dependencyProvider)
	{
		if (Dependencies.Count > 1)
		{
			throw new Exception("Dependency list has already been initialized.");
		}
		if (DependencyIdentifiers is not null)
		{
			string?[] dependencyNames = new string?[DependencyIdentifiers.Length + 1];
			dependencyNames[0] = Name;
			for (int i = 0; i < DependencyIdentifiers.Length; i++)
			{
				dependencyNames[i + 1] = DependencyIdentifiers[i].GetFilePath();
			}
			DependencyNames = dependencyNames;

			if (SelectiveLoadingMode.MetadataOnly)
			{
				DiscoveryDependencyIdentifiers = DependencyIdentifiers;
			}
			for (int i = 0; i < DependencyIdentifiers.Length; i++)
			{
				FileIdentifier identifier = DependencyIdentifiers[i];
				AssetCollection? dependency = Bundle.ResolveCollection(identifier);
				if (dependency is null)
				{
					dependencyProvider?.ReportMissingDependency(identifier);
				}
				SetDependency(i + 1, dependency);
			}
			DependencyIdentifiers = null;
		}
		else if (DependencyNames is null)
		{
			DependencyNames = [Name];
		}
	}

	/// <summary>
	/// Creates a <see cref="SerializedAssetCollection"/> from a <see cref="SerializedFile"/>.
	/// </summary>
	/// <remarks>
	/// The new <see cref="SerializedAssetCollection"/> is automatically added to the <paramref name="bundle"/>.
	/// </remarks>
	/// <param name="bundle">The <see cref="Bundle"/> to add this collection to.</param>
	/// <param name="file">The <see cref="SerializedFile"/> from which to make this collection.</param>
	/// <param name="factory">A factory for creating assets.</param>
	/// <param name="defaultVersion">The default version to use if the file does not have a version, ie the version has been stripped.</param>
	/// <returns>The new collection.</returns>
	internal static SerializedAssetCollection FromSerializedFile(Bundle bundle, SerializedFile file, AssetFactoryBase factory, UnityVersion defaultVersion = default)
	{
		UnityVersion version = file.Version.Equals(0, 0, 0) ? defaultVersion : file.Version;
		SerializedAssetCollection collection = new SerializedAssetCollection(bundle)
		{
			Name = file.NameFixed,
			Version = version,
			OriginalVersion = version,
			Platform = file.Platform,
			Flags = file.Flags,
			EndianType = file.EndianType,
		};
		if (SelectiveLoadingMode.MetadataOnly)
		{
			// Discovery needs the game file that contains each collection.
			collection.FilePath = file.FilePath;
		}
		ReadOnlySpan<FileIdentifier> fileDependencies = file.Dependencies;
		if (fileDependencies.Length > 0)
		{
			collection.DependencyIdentifiers = fileDependencies.ToArray();
		}
		ReadData(collection, file, factory);
		return collection;
	}

	private static void ReadData(SerializedAssetCollection collection, SerializedFile file, AssetFactoryBase factory)
	{
		SerializedTypeReference[] referenceTypes = file.RefTypes.ToArray();
		bool metadataOnly = SelectiveLoadingMode.MetadataOnly;
		for (int i = 0; i < file.Objects.Length; i++)
		{
			ObjectInfo objectInfo = file.Objects[i];
			int classID = objectInfo.TypeID < 0 ? 114 : objectInfo.TypeID;
			if (metadataOnly)
			{
				collection.DiscoveryClassIDs ??= new HashSet<int>();
				((HashSet<int>)collection.DiscoveryClassIDs).Add(classID);
				if (!SelectiveLoadingMode.MaterializedClassIDs.Contains(objectInfo.TypeID))
				{
					// In metadata-only mode only a few classes are materialized; the rest are
					// enumerated from the object tables without creating assets.
					continue;
				}
			}
			AssetInfo assetInfo = new AssetInfo(collection, objectInfo.FileID, classID);
			IUnityObjectBase? asset = factory.ReadAsset(assetInfo, objectInfo.ObjectData, objectInfo.Type, referenceTypes);
			if (asset is not null)
			{
				collection.AddAsset(asset);
			}
			// The raw serialized data is no longer needed once the asset has been read.
			// Releasing it here keeps the peak memory usage of large loads considerably lower.
			file.ReleaseObjectData(i);
		}
	}
}
