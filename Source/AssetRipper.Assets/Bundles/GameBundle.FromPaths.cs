using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.CompressedFiles;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using System.Runtime;

namespace AssetRipper.Assets.Bundles;

partial class GameBundle
{
	/// <summary>
	/// Create and initialize a <see cref="GameBundle"/> from a set of paths.
	/// </summary>
	/// <param name="paths">The set of paths to load.</param>
	/// <param name="assetFactory">The factory for reading assets.</param>
	/// <param name="dependencyProvider"></param>
	/// <param name="resourceProvider"></param>
	/// <param name="defaultVersion">The default version to use if a file does not have a version, ie the version has been stripped.</param>
	public static GameBundle FromPaths(IEnumerable<string> paths, AssetFactoryBase assetFactory, FileSystem fileSystem, IGameInitializer? initializer = null)
	{
		GameBundle gameBundle = new();
		initializer?.OnCreated(gameBundle, assetFactory);
		gameBundle.InitializeFromPaths(paths, assetFactory, fileSystem, initializer);
		initializer?.OnPathsLoaded(gameBundle, assetFactory);
		gameBundle.InitializeAllDependencyLists(initializer?.DependencyProvider);
		initializer?.OnDependenciesInitialized(gameBundle, assetFactory);
		return gameBundle;
	}

	private void InitializeFromPaths(IEnumerable<string> paths, AssetFactoryBase assetFactory, FileSystem fileSystem, IGameInitializer? initializer)
	{
		ResourceProvider = initializer?.ResourceProvider;
		if (IncrementalProcessing)
		{
			InitializeFromPathsIncrementally(paths, assetFactory, fileSystem, initializer);
			return;
		}

		List<FileBase> fileStack = LoadFilesAndDependencies(paths, fileSystem, initializer?.DependencyProvider);
		UnityVersion defaultVersion = initializer is null ? default : initializer.DefaultVersion;

		long collectionThreshold = MemoryPressureCollectionThreshold;
		long collectionBaseline = collectionThreshold > 0 ? GC.GetTotalMemory(forceFullCollection: true) : 0;
		while (fileStack.Count > 0)
		{
			ProcessFile(RemoveLastItem(fileStack), assetFactory, defaultVersion);

			if (collectionThreshold > 0 && GC.GetTotalMemory(forceFullCollection: false) - collectionBaseline > collectionThreshold)
			{
				GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
				GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
				collectionBaseline = GC.GetTotalMemory(forceFullCollection: true);
			}
		}
	}

	private void ProcessFile(FileBase file, AssetFactoryBase assetFactory, UnityVersion defaultVersion)
	{
		switch (file)
		{
			case SerializedFile serializedFile:
				SerializedAssetCollection.FromSerializedFile(this, serializedFile, assetFactory, defaultVersion);
				break;
			case FileContainer container:
				SerializedBundle serializedBundle = SerializedBundle.FromFileContainer(container, assetFactory, defaultVersion);
				AddBundle(serializedBundle);
				break;
			case ResourceFile resourceFile:
				AddResource(resourceFile);
				break;
			case FailedFile failedFile:
				AddFailed(failedFile);
				break;
		}
	}

	/// <summary>
	/// Loads and processes the game files one at a time. After a file has been processed its raw
	/// data is no longer referenced, so the garbage collector can reclaim it before the next file
	/// is loaded. Dependencies are resolved after each file instead of after all files, which is
	/// safe because the dependency lists of the collections are only initialized at the end.
	/// </summary>
	private void InitializeFromPathsIncrementally(IEnumerable<string> paths, AssetFactoryBase assetFactory, FileSystem fileSystem, IGameInitializer? initializer)
	{
		UnityVersion defaultVersion = initializer is null ? default : initializer.DefaultVersion;
		IDependencyProvider? dependencyProvider = initializer?.DependencyProvider;

		long collectionThreshold = MemoryPressureCollectionThreshold;
		long collectionBaseline = collectionThreshold > 0 ? GC.GetTotalMemory(forceFullCollection: true) : 0;
		void CollectIfNecessary()
		{
			if (collectionThreshold > 0 && GC.GetTotalMemory(forceFullCollection: false) - collectionBaseline > collectionThreshold)
			{
				GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
				GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
				collectionBaseline = GC.GetTotalMemory(forceFullCollection: true);
			}
		}

		HashSet<string> serializedFileNames = new();//Includes missing dependencies
		Queue<FileBase> fileQueue = new();
		foreach (string path in paths)
		{
			FileBase file = LoadFileWithContents(path, fileSystem);
			fileQueue.Enqueue(file);
			RegisterSerializedFileNames(file, serializedFileNames);
		}

		while (fileQueue.Count > 0)
		{
			FileBase file = fileQueue.Dequeue();
			ProcessFile(file, assetFactory, defaultVersion);

			foreach (SerializedFile serializedFile in EnumerateSerializedFiles(file))
			{
				foreach (FileIdentifier fileIdentifier in serializedFile.Dependencies)
				{
					string name = fileIdentifier.GetFilePath();
					if (serializedFileNames.Add(name) && dependencyProvider?.FindDependency(fileIdentifier) is { } dependency)
					{
						FileBase dependencyFile = LoadFileWithContents(dependency, fileSystem);
						fileQueue.Enqueue(dependencyFile);
						RegisterSerializedFileNames(dependencyFile, serializedFileNames);
					}
				}
			}

			CollectIfNecessary();
		}
	}

	private static FileBase LoadFileWithContents(string path, FileSystem fileSystem)
	{
		FileBase file;
		try
		{
			file = SchemeReader.LoadFile(path, fileSystem);
		}
		catch (Exception ex)
		{
			return new FailedFile()
			{
				Name = fileSystem.Path.GetFileName(path),
				FilePath = path,
				StackTrace = ex.ToString(),
			};
		}
		return LoadFileWithContents(file, fileSystem);
	}

	private static FileBase LoadFileWithContents(FileBase file, FileSystem fileSystem)
	{
		try
		{
			file.ReadContentsRecursively();
		}
		catch (Exception ex)
		{
			file = new FailedFile()
			{
				Name = file.Name,
				FilePath = file.FilePath,
				StackTrace = ex.ToString(),
			};
		}
		while (file is CompressedFile compressedFile)
		{
			file = compressedFile.UncompressedFile;
		}
		return file;
	}

	private static void RegisterSerializedFileNames(FileBase file, HashSet<string> serializedFileNames)
	{
		switch (file)
		{
			case SerializedFile serializedFile:
				serializedFileNames.Add(serializedFile.NameFixed);
				break;
			case FileContainer container:
				foreach (SerializedFile serializedFileInContainer in container.FetchSerializedFiles())
				{
					serializedFileNames.Add(serializedFileInContainer.NameFixed);
				}
				break;
		}
	}

	private static IEnumerable<SerializedFile> EnumerateSerializedFiles(FileBase file)
	{
		switch (file)
		{
			case SerializedFile serializedFile:
				yield return serializedFile;
				break;
			case FileContainer container:
				foreach (SerializedFile serializedFileInContainer in container.FetchSerializedFiles())
				{
					yield return serializedFileInContainer;
				}
				break;
		}
	}

	private static FileBase RemoveLastItem(List<FileBase> list)
	{
		int index = list.Count - 1;
		FileBase file = list[index];
		list.RemoveAt(index);
		return file;
	}

	private static List<FileBase> LoadFilesAndDependencies(IEnumerable<string> paths, FileSystem fileSystem, IDependencyProvider? dependencyProvider)
	{
		List<FileBase> files = new();
		HashSet<string> serializedFileNames = new();//Includes missing dependencies
		foreach (string path in paths)
		{
			FileBase? file;
			try
			{
				file = SchemeReader.LoadFile(path, fileSystem);
				file.ReadContentsRecursively();
			}
			catch (Exception ex)
			{
				file = new FailedFile()
				{
					Name = fileSystem.Path.GetFileName(path),
					FilePath = path,
					StackTrace = ex.ToString(),
				};
			}
			while (file is CompressedFile compressedFile)
			{
				file = compressedFile.UncompressedFile;
			}
			if (file is ResourceFile or FailedFile)
			{
				files.Add(file);
			}
			else if (file is SerializedFile serializedFile)
			{
				files.Add(file);
				serializedFileNames.Add(serializedFile.NameFixed);
			}
			else if (file is FileContainer container)
			{
				files.Add(file);
				foreach (SerializedFile serializedFileInContainer in container.FetchSerializedFiles())
				{
					serializedFileNames.Add(serializedFileInContainer.NameFixed);
				}
			}
		}

		for (int i = 0; i < files.Count; i++)
		{
			FileBase file = files[i];
			if (file is SerializedFile serializedFile)
			{
				LoadDependencies(serializedFile, files, serializedFileNames, dependencyProvider);
			}
			else if (file is FileContainer container)
			{
				foreach (SerializedFile serializedFileInContainer in container.FetchSerializedFiles())
				{
					LoadDependencies(serializedFileInContainer, files, serializedFileNames, dependencyProvider);
				}
			}
		}

		return files;
	}

	private static void LoadDependencies(SerializedFile serializedFile, List<FileBase> files, HashSet<string> serializedFileNames, IDependencyProvider? dependencyProvider)
	{
		foreach (FileIdentifier fileIdentifier in serializedFile.Dependencies)
		{
			string name = fileIdentifier.GetFilePath();
			if (serializedFileNames.Add(name) && dependencyProvider?.FindDependency(fileIdentifier) is { } dependency)
			{
				files.Add(dependency);
			}
		}
	}
}
