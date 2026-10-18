using AssetRipper.Import.Logging;
using System.Text.Json.Serialization;

namespace AssetRipper.Import.Configuration;

public sealed record class ImportSettings
{
	/// <summary>
	/// The level of scripts to export
	/// </summary>
	public ScriptContentLevel ScriptContentLevel { get; set; } = ScriptContentLevel.Level2;

	/// <summary>
	/// Including the streaming assets directory can cause some games to fail while exporting.
	/// </summary>
	[JsonIgnore]
	public bool IgnoreStreamingAssets
	{
		get => StreamingAssetsMode == StreamingAssetsMode.Ignore;
		set
		{
			StreamingAssetsMode = value ? StreamingAssetsMode.Ignore : StreamingAssetsMode.Extract;
		}
	}

	/// <summary>
	/// How the StreamingAssets folder is handled
	/// </summary>
	public StreamingAssetsMode StreamingAssetsMode { get; set; } = StreamingAssetsMode.Extract;

	/// <summary>
	/// The default version used when no version is specified, ie when the version has been stripped.
	/// </summary>
	[JsonConverter(typeof(UnityVersionJsonConverter))]
	public UnityVersion DefaultVersion { get; set; }

	/// <summary>
	/// The target version to convert all assets to. Experimental
	/// </summary>
	[JsonConverter(typeof(UnityVersionJsonConverter))]
	public UnityVersion TargetVersion { get; set; }

	/// <summary>
	/// An optional filter that is applied to the game file paths before they are loaded.
	/// Returning <see langword="false"/> skips the file. <see langword="null"/> loads every
	/// game file, which is the default (non-selective) behaviour.
	/// </summary>
	/// <remarks>
	/// Dependencies of the loaded files can still pull in additional files through the
	/// dependency provider. This option is intended for selective loading, where a discovery
	/// pass determines the files that contain the requested asset and its dependency closure.
	/// </remarks>
	[JsonIgnore]
	public Func<string, bool>? GameFilePathFilter { get; set; }

	public void Log()
	{
		Logger.Info(LogCategory.General, $"{nameof(ScriptContentLevel)}: {ScriptContentLevel}");
		Logger.Info(LogCategory.General, $"{nameof(StreamingAssetsMode)}: {StreamingAssetsMode}");
		Logger.Info(LogCategory.General, $"{nameof(DefaultVersion)}: {DefaultVersion}");
		Logger.Info(LogCategory.General, $"{nameof(TargetVersion)}: {TargetVersion}");
	}
}
