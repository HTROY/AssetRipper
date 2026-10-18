namespace AssetRipper.IO.Files.SerializedFiles;

/// <summary>
/// Opt-in, process-wide options for metadata-only (selective) game loading.
/// </summary>
/// <remarks>
/// Usually the importer eagerly reads the raw data of every serialized object while it parses
/// the metadata of a serialized file. For a very large game that is the dominant term of the
/// peak memory usage. When <see cref="MetadataOnly"/> is enabled, the importer remembers the
/// location and size of each object instead and reads only the data of the classes listed in
/// <see cref="MaterializedClassIDs"/>. That makes it possible to enumerate every collection,
/// its dependencies, and (through the asset bundle objects) the original asset paths without
/// materialising the whole game. A second, filtered load can then import the assets that are
/// actually needed.
/// </remarks>
public static class SelectiveLoadingMode
{
	private static readonly HashSet<int> materializedClassIDs = new();

	/// <summary>
	/// When true, object data is not eagerly read for classes that are not listed in
	/// <see cref="MaterializedClassIDs"/>. The default is <see langword="false"/>, which keeps
	/// the normal importer behaviour.
	/// </summary>
	public static bool MetadataOnly { get; set; }

	/// <summary>
	/// The class IDs whose object data is still read in <see cref="MetadataOnly"/> mode.
	/// </summary>
	public static IReadOnlyCollection<int> MaterializedClassIDs => materializedClassIDs;

	/// <summary>
	/// Replaces the set of class IDs whose object data is materialized in metadata-only mode.
	/// </summary>
	public static void SetMaterializedClassIDs(params int[] classIDs)
	{
		materializedClassIDs.Clear();
		foreach (int classID in classIDs)
		{
			materializedClassIDs.Add(classID);
		}
	}

	/// <summary>
	/// Returns true when the object data of the given class should be read.
	/// </summary>
	public static bool ShouldMaterialize(int typeID) => MetadataOnly && materializedClassIDs.Contains(typeID);

	/// <summary>
	/// Restores the normal importer behaviour.
	/// </summary>
	public static void Reset()
	{
		MetadataOnly = false;
		materializedClassIDs.Clear();
	}
}
